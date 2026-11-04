using System.Globalization;
using Microsoft.Win32;

namespace ValorantOptimizer.Core;

/// <summary>
/// Canonical, JSON-safe encoding of registry data:
/// DWord/QWord -> decimal string, String/ExpandString -> raw string,
/// Binary/None -> base64, MultiString -> newline separated.
/// </summary>
public static class RegistryValueCodec
{
    public static string Serialize(object? data, RegistryValueKind kind)
    {
        switch (kind)
        {
            case RegistryValueKind.DWord:
                uint dword = data switch
                {
                    int i => unchecked((uint)i),
                    uint u => u,
                    _ => Convert.ToUInt32(data, CultureInfo.InvariantCulture),
                };
                return dword.ToString(CultureInfo.InvariantCulture);

            case RegistryValueKind.QWord:
                ulong qword = data switch
                {
                    long l => unchecked((ulong)l),
                    ulong ul => ul,
                    _ => Convert.ToUInt64(data, CultureInfo.InvariantCulture),
                };
                return qword.ToString(CultureInfo.InvariantCulture);

            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
                return Convert.ToString(data, CultureInfo.InvariantCulture) ?? string.Empty;

            case RegistryValueKind.Binary:
            case RegistryValueKind.None:
                return Convert.ToBase64String(data as byte[] ?? []);

            case RegistryValueKind.MultiString:
                return string.Join("\n", data as string[] ?? []);

            case RegistryValueKind.Unknown:
                return Convert.ToString(data, CultureInfo.InvariantCulture) ?? string.Empty;

            default:
                throw new NotSupportedException($"Unsupported registry value kind '{kind}'.");
        }
    }

    public static object Deserialize(string serialized, RegistryValueKind kind)
    {
        switch (kind)
        {
            case RegistryValueKind.DWord:
                return unchecked((int)uint.Parse(serialized, CultureInfo.InvariantCulture));

            case RegistryValueKind.QWord:
                return unchecked((long)ulong.Parse(serialized, CultureInfo.InvariantCulture));

            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
                return serialized;

            case RegistryValueKind.Binary:
            case RegistryValueKind.None:
                return Convert.FromBase64String(serialized);

            case RegistryValueKind.MultiString:
                return serialized.Length == 0 ? [] : serialized.Split('\n');

            case RegistryValueKind.Unknown:
                return serialized;

            default:
                throw new NotSupportedException($"Unsupported registry value kind '{kind}'.");
        }
    }

    public static int ToRegistryDword(uint value) => unchecked((int)value);
}
