using System.Net;
using BlueChat.Turn;

namespace BlueChat.Turn.Tests;

public class PeerAddressPolicyTests
{
    public static TheoryData<string, bool> DenyPrivateCases => new()
    {
        { "203.0.113.9", true },
        { "8.8.8.8", true },
        { "2001:4860:4860::8888", true },
        { "10.0.0.1", false },
        { "10.255.255.255", false },
        { "172.16.0.1", false },
        { "172.31.255.254", false },
        { "172.32.0.1", true },
        { "192.168.1.1", false },
        { "100.64.0.1", false },
        { "100.127.255.254", false },
        { "100.128.0.1", true },
        { "127.0.0.1", false },
        { "127.255.0.1", false },
        { "169.254.1.1", false },
        { "169.254.169.254", false },
        { "0.0.0.0", false },
        { "0.1.2.3", false },
        { "224.0.0.1", false },
        { "239.255.255.250", false },
        { "255.255.255.255", false },
        { "240.0.0.1", false },
        { "198.18.0.1", false },
        { "192.0.0.170", false },
        { "::", false },
        { "::1", false },
        { "fe80::1", false },
        { "fec0::1", false },
        { "fc00::1", false },
        { "fd12:3456::1", false },
        { "ff02::1", false },
        { "fd00:ec2::254", false },
        { "::ffff:127.0.0.1", false },
        { "::ffff:10.1.2.3", false },
        { "::ffff:169.254.169.254", false },
        { "::ffff:8.8.8.8", true },
        { "::10.1.2.3", false },
        { "64:ff9b::a01:203", false },      // NAT64 of 10.1.2.3
        { "64:ff9b::808:808", true },       // NAT64 of 8.8.8.8
        { "2002:a01:203::1", false },       // 6to4 of 10.1.2.3
        { "2002:c0a8:101::1", false },      // 6to4 of 192.168.1.1
    };

    [Theory]
    [MemberData(nameof(DenyPrivateCases))]
    public void Deny_private_mode(string address, bool allowed) =>
        PeerAddressPolicy.DenyPrivate().IsAllowed(IPAddress.Parse(address)).Should().Be(allowed);

    [Theory]
    [InlineData("10.79.0.20", true)]
    [InlineData("10.79.0.21", true)]
    [InlineData("10.79.1.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::ffff:10.79.0.20", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    public void Allow_list_mode_relays_only_to_listed_networks(string address, bool allowed) =>
        PeerAddressPolicy.AllowList(["10.79.0.0/24"]).IsAllowed(IPAddress.Parse(address)).Should().Be(allowed);

    [Fact]
    public void Metadata_and_multicast_stay_forbidden_even_when_listed()
    {
        var policy = PeerAddressPolicy.AllowList(["169.254.0.0/16", "224.0.0.0/4", "127.0.0.0/8"]);
        policy.IsAllowed(IPAddress.Parse("169.254.169.254")).Should().BeFalse();
        policy.IsAllowed(IPAddress.Parse("224.0.0.1")).Should().BeFalse();
        policy.IsAllowed(IPAddress.Parse("169.254.1.1")).Should().BeTrue(); // listed explicitly
        policy.IsAllowed(IPAddress.Parse("127.0.0.1")).Should().BeTrue();   // listed explicitly (SFU on the same host)
    }

    [Fact]
    public void Empty_allow_list_relays_nothing()
    {
        PeerAddressPolicy.AllowList().IsAllowed(IPAddress.Parse("8.8.8.8")).Should().BeFalse();
        new TurnOptions().PeerPolicy.Mode.Should().Be(PeerPolicyMode.AllowList);
    }
}
