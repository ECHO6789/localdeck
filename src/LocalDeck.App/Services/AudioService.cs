using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace LocalDeck.App.Services;

public sealed class AudioService
{
    public IReadOnlyList<AudioSessionInfo> GetSessions()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            var result = new List<AudioSessionInfo>();
            for (var index = 0; index < sessions.Count; index++)
            {
                try
                {
                    var session = sessions[index];
                    if (session.State == AudioSessionState.AudioSessionStateExpired ||
                        session.IsSystemSoundsSession)
                    {
                        continue;
                    }

                    var processId = (int)session.GetProcessID;
                    var processName = "系统声音";
                    try
                    {
                        processName = System.Diagnostics.Process.GetProcessById(processId).ProcessName;
                    }
                    catch
                    {
                        // The process may have exited between enumeration and lookup.
                    }

                    result.Add(new AudioSessionInfo(
                        processId,
                        processName,
                        Math.Round(session.SimpleAudioVolume.Volume * 100),
                        session.SimpleAudioVolume.Mute));
                }
                catch
                {
                    // Session can become invalid during enumeration.
                }
            }

            return result
                .GroupBy(item => item.ProcessId)
                .Select(group => group.First())
                .OrderBy(item => item.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Audio enumeration failed: {ex.Message}");
            return [];
        }
    }

    public bool SetVolume(int processId, int percent, bool? muted = null)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            var changed = false;
            for (var index = 0; index < sessions.Count; index++)
            {
                var session = sessions[index];
                if (!session.IsSystemSoundsSession && session.GetProcessID == processId)
                {
                    session.SimpleAudioVolume.Volume = Math.Clamp(percent, 0, 100) / 100f;
                    if (muted.HasValue)
                    {
                        session.SimpleAudioVolume.Mute = muted.Value;
                    }

                    changed = true;
                }
            }

            return changed;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Audio control failed: {ex.Message}");
            return false;
        }
    }
}

public sealed record AudioSessionInfo(
    int ProcessId, string Name, double VolumePercent, bool Muted);
