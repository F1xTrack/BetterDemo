namespace BetterDemo.Interop.Wasapi;

public static class VirtualCableEndpointPolicy
{
    private static readonly string[] KnownRenderNames =
    [
        "CABLE Input",
        "CABLE-A Input",
        "CABLE-B Input",
        "VoiceMeeter Input",
        "VoiceMeeter Aux Input",
        "VoiceMeeter VAIO3 Input"
    ];

    public static bool IsVirtualCableRenderEndpoint(string friendlyName, string deviceFriendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return false;

        var knownName = KnownRenderNames.Any(name =>
            friendlyName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
            deviceFriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));
        var knownVendor = friendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
            deviceFriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
            deviceFriendlyName.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase);

        return knownName && knownVendor;
    }
}
