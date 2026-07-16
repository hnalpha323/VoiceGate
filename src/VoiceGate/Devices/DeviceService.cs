using NAudio.CoreAudioApi;

namespace VoiceGate.Devices;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsVbCable)
{
    public override string ToString() => Name;
}

/// <summary>Enumerates audio endpoints and locates the VB-Audio virtual cable.</summary>
public static class DeviceService
{
    public static List<AudioDeviceInfo> GetDevices(DataFlow flow)
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            var enumerator = new MMDeviceEnumerator();
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                string name;
                try
                {
                    name = device.FriendlyName;
                }
                catch { continue; }
                bool cable = name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
                          && name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);
                list.Add(new AudioDeviceInfo(device.ID, name, cable));
            }
        }
        catch
        {
            // Broken/stopped Windows Audio service: degrade to an empty list so the
            // app can still open and show the problem instead of dying at startup.
        }
        return list;
    }

    /// <summary>The cable's playback side ("CABLE Input") - where VoiceGate sends processed audio.</summary>
    public static AudioDeviceInfo? FindCableRender()
        => GetDevices(DataFlow.Render).FirstOrDefault(d => d.IsVbCable);

    /// <summary>The cable's recording side ("CABLE Output") - what Discord should use as its mic.</summary>
    public static AudioDeviceInfo? FindCableCapture()
        => GetDevices(DataFlow.Capture).FirstOrDefault(d => d.IsVbCable);

    public static bool IsVbCableInstalled => FindCableRender() != null;

    public static string? GetDefaultCaptureId()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID;
        }
        catch
        {
            return null;
        }
    }
}
