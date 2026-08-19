namespace BeastStrap.Models
{
    // Lightweight snapshot of a running RobloxPlayerBeta process for the Instances panel.
    public record RobloxInstanceInfo(int Pid, string Uptime, long MemoryMb, string WindowTitle);
}
