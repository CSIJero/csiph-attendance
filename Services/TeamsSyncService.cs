using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AttendanceMonitoring.Services
{
    /// <summary>
    /// Placeholder for future Microsoft Teams sync/integration.
    /// </summary>
    public class TeamsSyncService
    {
        private readonly ILogger<TeamsSyncService> _log;
        public TeamsSyncService(ILogger<TeamsSyncService> log)
        {
            _log = log;
        }

        public Task SyncUserStatusAsync(int userId)
        {
            _log.LogInformation("[TeamsSync] Placeholder: would sync status for user {UserId}", userId);
            return Task.CompletedTask;
        }
    }
}
