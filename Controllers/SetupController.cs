using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SetupController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SetupController> _logger;

    public SetupController(IConfiguration configuration, ILogger<SetupController> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("create-analytics-table")]
    public async Task<ActionResult> CreateAnalyticsTable()
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            return BadRequest("Connection string not configured.");

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = @"
create table if not exists public.page_views (
  id uuid primary key default gen_random_uuid(),
  country text,
  device text,
  created_at timestamp with time zone default now()
);

alter table public.page_views enable row level security;

create policy if not exists ""page_views_insert"" on public.page_views
  for insert to anon with check (true);

create policy if not exists ""page_views_select"" on public.page_views
  for select to authenticated with check (true);

create index if not exists page_views_created_at on public.page_views(created_at);
create index if not exists page_views_country on public.page_views(country);
create index if not exists page_views_device on public.page_views(device);
            ";

            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();

            _logger.LogInformation("Analytics table created successfully");
            return Ok(new { success = true, message = "Analytics table created" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create analytics table");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
