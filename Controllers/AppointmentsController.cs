using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using App.DTOs;

namespace App.Controllers;

[Route("api/[controller]")] 
[ApiController]
public class AppointmentsController : ControllerBase
{
    private readonly string _connectionString;

    public AppointmentsController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
                            ?? throw new InvalidOperationException("Connection string not found");
    }

    [HttpGet]
    public async Task<IActionResult> GetAppointments([FromQuery] string? status, [FromQuery] string? patientLastName)
    {
        
        try
        {
            var appointments = new List<AppointmentListDto>();

            await using var connection = new SqlConnection(_connectionString);

            const string sql = @"SELECT
                a.IdAppointment,
                a.AppointmentDate,
                a.Status,
                a.Reason,
                p.FirstName + N' ' + p.LastName AS PatientFullName,
                p.Email AS PatientEmail
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
            ORDER BY a.AppointmentDate";

            await using var command = new SqlCommand(sql, connection);

            command.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
            command.Parameters.AddWithValue("@PatientLastName", (object?)patientLastName ?? DBNull.Value);

            await connection.OpenAsync();

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                appointments.Add(new AppointmentListDto
                {
                    IdAppointment = (int)reader["IdAppointment"],
                    AppointmentDate = (DateTime)reader["AppointmentDate"],
                    Status = (string)reader["Status"],
                    Reason = (string)reader["Reason"],
                    PatientFullName = (string)reader["PatientFullName"],
                    PatientEmail = (string)reader["PatientEmail"]
                });
            }

            return Ok(appointments);
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
    
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetAppointmentDetails(int id)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            const string sql = @"
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, a.InternalNotes,
                   p.Email AS PatientEmail, p.PhoneNumber AS PatientPhone, 
                   d.LicenseNumber AS DoctorLicenseNumber
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON a.IdPatient = p.IdPatient
            JOIN dbo.Doctors d ON a.IdDoctor = d.IdDoctor
            WHERE a.IdAppointment = @Id";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Id", id);

            await connection.OpenAsync();
            await using var reader = await command.ExecuteReaderAsync();

            if (!await reader.ReadAsync()) return NotFound($"Appointment {id} not found.");

            return Ok(new AppointmentDetailsDto {
                IdAppointment = (int)reader["IdAppointment"],
                AppointmentDate = (DateTime)reader["AppointmentDate"],
                Status = (string)reader["Status"],
                Reason = (string)reader["Reason"],
                InternalNotes = reader["InternalNotes"] == DBNull.Value ? null : (string)reader["InternalNotes"],
                PatientEmail = (string)reader["PatientEmail"],
                PatientPhone = (string)reader["PatientPhone"],
                DoctorLicenseNumber = (string)reader["DoctorLicenseNumber"]
            });
        }
        catch (Exception ex) { return StatusCode(500, ex.Message); }
    }

    [HttpPost]
public async Task<IActionResult> AddAppointment([FromBody] CreateAppointmentRequestDto request)
{
    if (request.AppointmentDate < DateTime.Now) return BadRequest("Date cannot be in the past.");

    await using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    try
    {
        const string activeCheckSql = @"
            SELECT 
                (SELECT IsActive FROM dbo.Patients WHERE IdPatient = @IdP) as PatientActive,
                (SELECT IsActive FROM dbo.Doctors WHERE IdDoctor = @IdD) as DoctorActive";
        
        await using var activeCmd = new SqlCommand(activeCheckSql, connection, (SqlTransaction)transaction);
        activeCmd.Parameters.AddWithValue("@IdP", request.IdPatient);
        activeCmd.Parameters.AddWithValue("@IdD", request.IdDoctor);

        await using (var reader = await activeCmd.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync() || reader["PatientActive"] == DBNull.Value || reader["DoctorActive"] == DBNull.Value)
                return BadRequest("Patient or Doctor does not exist.");
            
            if (!(bool)reader["PatientActive"] || !(bool)reader["DoctorActive"])
                return BadRequest("Patient or Doctor is currently inactive.");
        }

        const string conflictSql = "SELECT COUNT(*) FROM dbo.Appointments WHERE IdDoctor = @IdD AND AppointmentDate = @Date AND Status = 'Scheduled'";
        await using var conflictCmd = new SqlCommand(conflictSql, connection, (SqlTransaction)transaction);
        conflictCmd.Parameters.AddWithValue("@IdD", request.IdDoctor);
        conflictCmd.Parameters.AddWithValue("@Date", request.AppointmentDate);
        
        if ((int)await conflictCmd.ExecuteScalarAsync() > 0) return Conflict("Doctor already has an appointment at this time.");

        const string sql = @"INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
                            OUTPUT INSERTED.IdAppointment
                            VALUES (@IdP, @IdD, @Date, 'Scheduled', @Reason)";

        await using var command = new SqlCommand(sql, connection, (SqlTransaction)transaction);
        command.Parameters.AddWithValue("@IdP", request.IdPatient);
        command.Parameters.AddWithValue("@IdD", request.IdDoctor);
        command.Parameters.AddWithValue("@Date", request.AppointmentDate);
        command.Parameters.AddWithValue("@Reason", request.Reason);

        var newId = (int)await command.ExecuteScalarAsync();
        await transaction.CommitAsync();

        return CreatedAtAction(nameof(GetAppointmentDetails), new { id = newId }, new { Id = newId });
    }
    catch (Exception ex)
    {
        await transaction.RollbackAsync();
        return StatusCode(500, ex.Message);
    }
}

[HttpPut("{idAppointment:int}")]
public async Task<IActionResult> UpdateAppointment(int idAppointment, [FromBody] UpdateAppointmentRequestDto request)
{
    var Statuses = new[] { "Scheduled", "Completed", "Cancelled" };
    if (!Statuses.Contains(request.Status)) return BadRequest("Invalid status name");
    
    await using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    try
    {
        const string getSql = "SELECT Status, AppointmentDate FROM dbo.Appointments WHERE IdAppointment = @Id";
        await using var command = new SqlCommand(getSql, connection, (SqlTransaction)transaction);
        command.Parameters.AddWithValue("@Id", idAppointment);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return NotFound("Appointment not found.");

        string currentStatus = reader.GetString(0); 
        DateTime currentDate = reader.GetDateTime(1);
        await reader.CloseAsync();

        if (currentStatus == "Completed" && currentDate != request.AppointmentDate)
        {
            return Conflict("Cannot change the date of a completed appointment.");
        }

        if (currentDate != request.AppointmentDate)
        {
            const string conflictSql = @"SELECT COUNT(*) FROM dbo.Appointments 
                                        WHERE IdDoctor = @IdD AND AppointmentDate = @Date 
                                        AND IdAppointment != @Id AND Status = 'Scheduled'";
            await using var conflict = new SqlCommand(conflictSql, connection, (SqlTransaction)transaction);
            conflict.Parameters.AddWithValue("@IdD", request.IdDoctor);
            conflict.Parameters.AddWithValue("@Date", request.AppointmentDate);
            conflict.Parameters.AddWithValue("@Id", idAppointment);

            if ((int)await conflict.ExecuteScalarAsync() > 0)
                return Conflict("Doctor already has an appointment at this time.");
        }

        const string updateSql = @"UPDATE dbo.Appointments 
                              SET IdPatient = @IdP, IdDoctor = @IdD, AppointmentDate = @Date, 
                                  Status = @Status, Reason = @Reason, InternalNotes = @Notes
                              WHERE IdAppointment = @Id";

        await using var updateCommand = new SqlCommand(updateSql, connection, (SqlTransaction)transaction);
        updateCommand.Parameters.AddWithValue("@IdP", request.IdPatient);
        updateCommand.Parameters.AddWithValue("@IdD", request.IdDoctor);
        updateCommand.Parameters.AddWithValue("@Date", request.AppointmentDate);
        updateCommand.Parameters.AddWithValue("@Status", request.Status);
        updateCommand.Parameters.AddWithValue("@Reason", request.Reason);
        updateCommand.Parameters.AddWithValue("@Notes", (object?)request.InternalNotes ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("@Id", idAppointment);

        await updateCommand.ExecuteNonQueryAsync();
        await transaction.CommitAsync();

        return Ok();
    }
    catch (Exception ex)
    {
        await transaction.RollbackAsync();
        return StatusCode(500, ex.Message);
    }
}


    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAppointment(int id)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            const string checkSql = "SELECT Status FROM dbo.Appointments WHERE IdAppointment = @Id";
            await using var checkCmd = new SqlCommand(checkSql, connection);
            checkCmd.Parameters.AddWithValue("@Id", id);
        
            var status = await checkCmd.ExecuteScalarAsync() as string;

            if (status == null) return NotFound();
            if (status == "Completed") return Conflict("Cannot delete a completed appointment.");

            const string deleteSql = "DELETE FROM dbo.Appointments WHERE IdAppointment = @Id";
            await using var deleteCmd = new SqlCommand(deleteSql, connection);
            deleteCmd.Parameters.AddWithValue("@Id", id);
            await deleteCmd.ExecuteNonQueryAsync();

            return NoContent();
        }
        catch (Exception ex) { return StatusCode(500, ex.Message); }
    }
}
