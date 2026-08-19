namespace BeastStrap.Models
{
    internal class WatcherData
    {
        public int ProcessId { get; set; }

        public string? LogFile { get; set; }

        public List<int>? AutoclosePids { get; set; }

        // Main window handle of the launched client, captured in StartRoblox so the watcher can
        // rewrite the window (icon / title / fake borderless). Zero when the client had no window
        // yet or window manipulation is off.
        public long Handle { get; set; }
    }
}
