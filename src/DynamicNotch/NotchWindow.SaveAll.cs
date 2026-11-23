using System.Text.Json;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Everything you keep, saved at once on this thread: Windows shutting down, Pixo closing, or an update asking it to
/// close first. Each store is written safely (Services/SafeFile) and skipped if its file couldn't be read when Pixo
/// started (it's kept as it is). Background saves still on their way can't land over these (they're older).
/// </summary>
public partial class NotchWindow
{
    /// <summary>
    /// Saved data that couldn't be read when Pixo started (cut short by a crash, or held by another app): it's kept as it
    /// is, never saved over, and you're told once, with the folder a click away.
    /// </summary>
    void TellFileTroubles()
    {
        var names = new List<string>();
        while (SafeFile.Troubles.TryDequeue(out var n)) if (!names.Contains(n)) names.Add(n);
        if (names.Count == 0) return;
        ShowOsd("files", "", null, Loc.T("Some saved data couldn't be read"), string.Join(", ", names) + " · " + Loc.T("kept as it is, click to see the folder"), null,
            seconds: 6, onClick: () => Launch(Settings.Folder));
    }

    public void SaveEverythingNow()
    {
        static void Try(string what, Action save)
        {
            try { save(); }
            catch (Exception ex) { AppLog.Write($"saving on exit ({what}): {ex.Message}"); }
        }
        Try("notes", SaveNotesOnExit);
        Try("clipboard", () =>
        {
            if (_clipsUnreadable) return;
            var snapshot = _clips.Select(c => new ClipEntry { Text = c.Text, Image = c.Image, At = c.At, Pinned = c.Pinned, Hash = c.Hash }).ToList();
            SafeFile.WriteText(ClipFile, JsonSerializer.Serialize(snapshot), SafeFile.Stamp());
        });
        Try("shelf", () => { if (!_shelfUnreadable) SafeFile.WriteText(ShelfFile, JsonSerializer.Serialize(_shelf.ToList()), SafeFile.Stamp()); });
        Try("reminders", () =>
        {
            if (_remindersUnreadable) return;
            var snapshot = _reminders.Select(r => new ReminderItem { Text = r.Text, At = r.At }).ToList();
            SafeFile.WriteText(RemindersFile, JsonSerializer.Serialize(snapshot), SafeFile.Stamp());
        });
        Try("calls", () => { if (!_historyUnreadable) SafeFile.WriteText(HistoryFile, JsonSerializer.Serialize(_history.ToList()), SafeFile.Stamp()); });
        Try("app time", () => { if (_appTime != null && !_appTimeUnreadable) SafeFile.WriteText(AppTimeFile, JsonSerializer.Serialize(_appTime), SafeFile.Stamp()); });
        Try("work", WorkLog.Save);
        Try("notifications", () => NotifHistory.Flush()); // its writes wait in a queue: written before Pixo goes
        Try("settings", _settings.SaveNow);
        AppLog.Flush();
    }
}
