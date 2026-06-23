namespace AttendanceMonitoring.Services;

public interface IEmailSender
{
    /// <summary>
    /// Send an email to the given recipients. When <see cref="EmailOptions.Enabled"/>
    /// is false, the message is logged at Information level instead of being sent —
    /// useful for development and CI.
    /// </summary>
    Task SendAsync(
        IEnumerable<string> to,
        string subject,
        string body,
        CancellationToken ct = default);
}
