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
        
        command.Parameters.AddWithValue("@Status",(object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("@PatientLastName",(object?)patientLastName ?? DBNull.Value);

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
}