using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace SysBot.Pokemon;

/// <summary>
/// Settings for Tailscale network integration and multi-node bot management
/// </summary>
public sealed class TailscaleSettings
{
    private const string Network = nameof(Network);
    private const string NodeManagement = nameof(NodeManagement);

    [Category(Network), Description("Enable Tailscale network integration for multi-node bot management.")]
    public bool Enabled { get; set; } = false;

    [Category(Network), Description("List of Tailscale IP addresses to scan for remote bot instances. Format: 100.x.x.x")]
    public List<string> RemoteNodes { get; set; } = new();

    [Category(NodeManagement), Description("Starting port for scanning remote bot instances.")]
    public int PortScanStart { get; set; } = 8081;

    [Category(NodeManagement), Description("Ending port for scanning remote bot instances.")]
    public int PortScanEnd { get; set; } = 8110;

    [Category(NodeManagement), Description("This node acts as the master dashboard aggregating all remote nodes.")]
    public bool IsMasterNode { get; set; } = false;

    [Category(NodeManagement), Description("IP address of the master dashboard node. Leave empty if this is the master.")]
    public string MasterNodeIP { get; set; } = "";

    [Category(NodeManagement), Description("Port allocation strategy for avoiding conflicts between nodes.")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public TailscalePortAllocation PortAllocation { get; set; } = new();

    [Category(Network), Description("Timeout in seconds for remote node connections.")]
    public int ConnectionTimeoutSeconds { get; set; } = 5;

    [Category(Network), Description("Interval in seconds between remote node scans.")]
    public int ScanIntervalSeconds { get; set; } = 30;
}

/// <summary>
/// Port allocation settings for different Tailscale nodes to prevent conflicts
/// </summary>
public sealed class TailscalePortAllocation
{
    [Description("Port ranges allocated to specific IP addresses to prevent conflicts.")]
    public Dictionary<string, TailscalePortRange> NodeAllocations { get; set; } = new();

    [Description("Default port range for nodes not explicitly configured.")]
    public TailscalePortRange DefaultRange { get; set; } = new() { Start = 8101, End = 8110 };
}

/// <summary>
/// Port range configuration for a Tailscale node
/// </summary>
public sealed class TailscalePortRange
{
    [Description("Starting port number for this node.")]
    public int Start { get; set; } = 8081;

    [Description("Ending port number for this node.")]
    public int End { get; set; } = 8090;

    public bool ContainsPort(int port) => port >= Start && port <= End;
    public IEnumerable<int> GetPortRange() => Enumerable.Range(Start, End - Start + 1);
} 