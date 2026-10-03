using System.Net.NetworkInformation;
using HandRaise.Infrastructure.Windows.Hardware;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class NetworkAddressHelperTests
{
    [Fact]
    public void GetPreferredLanIpv4Address_WhenAddressFound_IsNotLoopbackOrApipa()
    {
        var lanIp = NetworkAddressHelper.GetPreferredLanIpv4Address();
        if (lanIp is not null)
        {
            Assert.False(lanIp.StartsWith("127.", StringComparison.OrdinalIgnoreCase));
            Assert.False(lanIp.StartsWith("169.254.", StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(lanIp));
        }
    }
}
