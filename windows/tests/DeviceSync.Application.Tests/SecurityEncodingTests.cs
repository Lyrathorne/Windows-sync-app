using DeviceSync.Application;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class SecurityEncodingTests
{
    [Fact]
    public void TranscriptAndHmac_MatchSharedVector()
    {
        var secret = SecurityEncoding.Base64UrlDecode("QEFCQ0RFRkdISUpLTE1OT1BRUlNUVVZXWFlaW1xdXl8");
        var transcript = TranscriptBuilder.PairingRequest(
            "pair-test",
            "windows-test",
            "android-test",
            "YvXFIt0M1gFOAKkY-_p23sih4W_t9CcrvjMIAEzgBIs",
            "alipEjFnUgu58tJFM3B0sakZlgxX3STzwQHbslRboxU",
            "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8");

        Assert.Equal(
            "AAAAE0RldmljZVN5bmNQYWlyaW5nVjEAAAAJcGFpci10ZXN0AAAADHdpbmRvd3MtdGVzdAAAAAxhbmRyb2lkLXRlc3QAAAArWXZYRkl0ME0xZ0ZPQUtrWS1fcDIzc2loNFdfdDlDY3J2ak1JQUV6Z0JJcwAAACthbGlwRWpGblVndTU4dEpGTTNCMHNha1psZ3hYM1NUendRSGJzbFJib3hVAAAAK0FBRUNBd1FGQmdjSUNRb0xEQTBPRHhBUkVoTVVGUllYR0JrYUd4d2RIaDg",
            SecurityEncoding.Base64UrlEncode(transcript));
        Assert.Equal(
            "WRaXaNsmNgqpZCKuGh0NX5_qWD0958ZO-V4q5w7YGV0",
            SecurityEncoding.Base64UrlEncode(SecurityEncoding.HmacSha256(secret, transcript)));
    }

    [Fact]
    public void VerificationCode_MatchesSharedVector()
    {
        var code = SecurityEncoding.VerificationCode(
            "DeviceSyncPairingChallengeV1",
            "pair-test",
            "windows-test",
            "android-test",
            "YvXFIt0M1gFOAKkY-_p23sih4W_t9CcrvjMIAEzgBIs",
            "alipEjFnUgu58tJFM3B0sakZlgxX3STzwQHbslRboxU",
            "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",
            "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8");

        Assert.Equal("757216", code);
        Assert.Matches("^[0-9]{6}$", code);
    }
}
