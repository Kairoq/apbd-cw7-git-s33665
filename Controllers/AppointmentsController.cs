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

    [HttpPost]
    public async Task<IActionResult> AddAppointment([FromBody] CreateAppointmentRequestDto request)
    {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            
            if (request.AppointmentDate < DateTime.Now)
            {
                return BadRequest("Appointment date cannot be in the past.");
            }
            try
            {

                const string sql = @"INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
                OUTPUT INSERTED.IdAppointment
                VALUES (@IdPatient, @IdDoctor, @Date, 'Scheduled', @Reason)";

                await using var command = new SqlCommand(sql, connection, (SqlTransaction)transaction);
                command.Parameters.AddWithValue("@IdPatient", request.IdPatient);
                command.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
                command.Parameters.AddWithValue("@Date", request.AppointmentDate);
                command.Parameters.AddWithValue("@Reason", request.Reason);

                var newId = (int)await command.ExecuteScalarAsync();

                const string sqlPatientUpdate = @"UPDATE dbo.Patients SET LastName = LastName WHERE IdPatient = @IdPatient";
                
                await using var commandPatient = new SqlCommand(sqlPatientUpdate, connection, (SqlTransaction)transaction);
                commandPatient.Parameters.AddWithValue("@IdPatient", request.IdPatient);
                await commandPatient.ExecuteNonQueryAsync();

                await transaction.CommitAsync();

                return CreatedAtAction(nameof(GetAppointments), new { Id = newId }, request);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, $"Executed rollback: {ex.Message }");
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