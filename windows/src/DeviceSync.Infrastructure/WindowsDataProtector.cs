using System.Security.Cryptography;
using System.Runtime.Versioning;
using DeviceSync.Application;

namespace DeviceSync.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class WindowsDataProtector : IDataProtector
{
    public byte[] Protect(byte[] plainBytes)
    {
        return ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedBytes)
    {
        return ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }
}
