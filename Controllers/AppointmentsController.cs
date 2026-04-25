using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using ConsoleApp1.DTOs;

namespace ConsoleApp1.Controllers;

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
}