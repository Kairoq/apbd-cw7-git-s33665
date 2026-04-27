using System.Transactions;
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
                SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason,
                       p.Email, p.PhoneNumber, d.LicenseNumber
                FROM dbo.Appointments a
                JOIN dbo.Patients p ON a.IdPatient = p.IdPatient
                JOIN dbo.Doctors d ON a.IdDoctor = d.IdDoctor
                WHERE a.IdAppointment = @Id";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Id", id);

            await connection.OpenAsync();
            await using var reader = await command.ExecuteReaderAsync();

            if (!await reader.ReadAsync()) return NotFound($"Appointment {id} not found.");

            return Ok(new AppointmentListDto {
                IdAppointment = (int)reader["IdAppointment"],
                AppointmentDate = (DateTime)reader["AppointmentDate"],
                Status = (string)reader["Status"],
                Reason = (string)reader["Reason"],
                PatientEmail = (string)reader["PatientEmail"],

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
    
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> DeleteAppointment(int id)
        {
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                const string sql = @"DELETE FROM dbo.Appointments WHERE IdAppointment = @Id";
                
                await using var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@Id", id);
                
                await connection.OpenAsync();
                var rowsAffected = await command.ExecuteNonQueryAsync();

                if (rowsAffected == 0)
                {
                    return NotFound($"Appointment Id {id} not found");
                }
                
                return Ok(new {message = $"Successfully deleted appointment {id}"});
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }
}