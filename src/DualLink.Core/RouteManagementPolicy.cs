namespace DualLink.Core;

public enum RouteManagementMode
{
    Physical,
    WireGuard,
    WaitingForWireGuardEndpoint,
    RelayBonding,
    ConflictingTunnels
}

public static class RouteManagementPolicy
{
    // The running tunnel determines route ownership. A saved UI preference must
    // never make ordinary VPN traffic prove that it bypasses its own tunnel.
    // Endpoint discovery is asynchronous; wait for it without falling back to
    // physical route mutations or stopping health sampling/dashboard updates.
    public static RouteManagementMode Resolve(bool wireGuardDetected, bool bondingActive,
        bool wireGuardEndpointKnown)
    {
        if (bondingActive)
            return wireGuardDetected ? RouteManagementMode.ConflictingTunnels : RouteManagementMode.RelayBonding;
        if (wireGuardDetected)
            return wireGuardEndpointKnown ? RouteManagementMode.WireGuard : RouteManagementMode.WaitingForWireGuardEndpoint;
        return RouteManagementMode.Physical;
    }
}
