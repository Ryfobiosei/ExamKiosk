using System.Management;
using NAudio.CoreAudioApi;
using System.Net.NetworkInformation;

namespace ExamKiosk.Services;

public sealed class WindowsDeviceControls
{
    public double? GetBrightness()
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\WMI",
            "SELECT CurrentBrightness FROM WmiMonitorBrightness");
        using var monitors = searcher.Get();

        foreach (ManagementObject monitor in monitors)
        {
            using (monitor)
            {
                return Convert.ToDouble(monitor["CurrentBrightness"]);
            }
        }

        return null;
    }

    public void SetBrightness(byte brightness)
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\WMI",
            "SELECT * FROM WmiMonitorBrightnessMethods");
        using var monitors = searcher.Get();
        var updated = false;

        foreach (ManagementObject monitor in monitors)
        {
            using (monitor)
            using (var parameters = monitor.GetMethodParameters("WmiSetBrightness"))
            {
                parameters["Timeout"] = (uint)1;
                parameters["Brightness"] = brightness;
                using var result = monitor.InvokeMethod("WmiSetBrightness", parameters, null);
                updated = true;
            }
        }

        if (!updated)
        {
            throw new InvalidOperationException("This display does not expose Windows brightness controls.");
        }
    }

    public float? GetMasterVolume()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        return device.AudioEndpointVolume.MasterVolumeLevelScalar;
    }

    public void SetMasterVolume(float volume)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);
    }

    public string GetWifiStatus()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .ToArray();

        if (interfaces.Length == 0)
        {
            return "No Wi-Fi adapter detected";
        }

        return interfaces.Any(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
            ? "Connected"
            : "Not connected";
    }
}