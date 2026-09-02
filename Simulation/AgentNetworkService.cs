using Friflo.Engine.ECS;

namespace ProxyState.Simulation;

/// <summary>
/// Owns all runtime network mutations and keeps relation data consistent when
/// agents or network entities are removed. Callers should never add an
/// <see cref="AgentNetworkMembership"/> directly.
/// </summary>
public sealed class AgentNetworkService : IDisposable
{
    private readonly EntityStore _store;
    private readonly AgentNetworkCatalog _catalog;
    private readonly AgentLodService? _lodService;
    private bool _handlingDeletion;

    public AgentNetworkService(EntityStore store, AgentNetworkCatalog catalog, AgentLodService? lodService = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _lodService = lodService;
        _store.OnEntityDelete += HandleEntityDelete;
    }

    public Entity CreateNetwork(int typeHash, int anchorLocationId, int ordinal)
    {
        _catalog.GetType(typeHash);
        if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        return _store.CreateEntity(new AgentNetworkData
        {
            TypeHash = typeHash,
            AnchorLocationId = anchorLocationId,
            Ordinal = ordinal
        });
    }

    public void DeleteNetwork(Entity network)
    {
        RequireNetwork(network);
        RemoveAllNetworkMemberships(network);
        network.DeleteEntity();
    }

    public void AddMembership(Entity agent, Entity network, int roleHash, Entity supervisor = default)
    {
        RequireAgent(agent);
        var type = RequireNetwork(network);
        RequireRole(type, roleHash);
        if (agent.TryGetRelation<AgentNetworkMembership, Entity>(network, out _))
            throw new InvalidOperationException("The agent already belongs to this network.");

        var sameTypeCount = 0;
        foreach (var membership in agent.GetRelations<AgentNetworkMembership>())
        {
            if (!membership.Network.IsNull && membership.Network.TryGetComponent<AgentNetworkData>(out var data) && data.TypeHash == type.Hash)
                sameTypeCount++;
        }
        if (sameTypeCount >= type.MaxNetworksPerAgent)
            throw new InvalidOperationException($"The agent has reached the '{type.Id}' membership limit.");

        ValidateSupervisor(agent, network, type, supervisor, adding: true);
        agent.AddRelation(new AgentNetworkMembership { Network = network, RoleHash = roleHash, Supervisor = supervisor });
        InvalidateNetworkMembers(network, agent);
        NotifyLodNetworkMembers(network, agent, supervisor);
    }

    public AgentNetworkMembership GetMembership(Entity agent, Entity network)
    {
        RequireAgent(agent);
        RequireNetwork(network);
        return agent.TryGetRelation<AgentNetworkMembership, Entity>(network, out var membership)
            ? membership
            : throw new KeyNotFoundException("The agent does not belong to the network.");
    }

    public IReadOnlyList<AgentNetworkMembership> GetMemberships(Entity agent)
    {
        RequireAgent(agent);
        var result = new List<AgentNetworkMembership>();
        foreach (var membership in agent.GetRelations<AgentNetworkMembership>()) result.Add(membership);
        return result;
    }

    public IReadOnlyList<(Entity Agent, AgentNetworkMembership Membership)> GetNetworkMembers(Entity network)
    {
        RequireNetwork(network);
        var result = new List<(Entity, AgentNetworkMembership)>();
        foreach (var link in network.GetIncomingLinks<AgentNetworkMembership>())
            if (link.Entity.TryGetRelation<AgentNetworkMembership, Entity>(network, out var membership)) result.Add((link.Entity, membership));
        return result;
    }

    public void ChangeRole(Entity agent, Entity network, int roleHash)
    {
        var type = RequireNetwork(network);
        RequireAgent(agent);
        RequireRole(type, roleHash);
        ref var membership = ref GetMembershipReference(agent, network);
        membership.RoleHash = roleHash;
        InvalidateNetworkMembers(network, agent);
    }

    public void ChangeSupervisor(Entity agent, Entity network, Entity supervisor)
    {
        RequireAgent(agent);
        var type = RequireNetwork(network);
        GetMembershipReference(agent, network);
        ValidateSupervisor(agent, network, type, supervisor, adding: false);
        ref var membership = ref GetMembershipReference(agent, network);
        var previousSupervisor = membership.Supervisor;
        membership.Supervisor = supervisor;
        InvalidateNetworkMembers(network, agent);
        NotifyLodNetworkMembers(network, agent, previousSupervisor, supervisor);
    }

    /// <summary>
    /// Removes a member. Direct reports of a manager are reassigned to that
    /// manager's supervisor. Removing a root requires an explicit successor
    /// unless it is the network's final member.
    /// </summary>
    public void RemoveMembership(Entity agent, Entity network, Entity successor = default)
    {
        RequireAgent(agent);
        var type = RequireNetwork(network);
        var membership = GetMembership(agent, network);
        RemoveMembershipCore(agent, network, type, membership, successor, deletingAgent: false);
    }

    public void Dispose() => _store.OnEntityDelete -= HandleEntityDelete;

    private void RemoveMembershipCore(Entity agent, Entity network, NetworkTypeDefinition type,
        AgentNetworkMembership membership, Entity successor, bool deletingAgent)
    {
        if (type.HierarchyMode == NetworkHierarchyMode.Flat)
        {
            agent.RemoveRelation<AgentNetworkMembership>(network);
            InvalidateNetworkMembers(network, agent);
            NotifyLodNetworkMembers(network, agent);
            return;
        }

        var members = GetNetworkMembers(network);
        var reports = members.Where(item => item.Membership.Supervisor == agent).Select(item => item.Agent).ToArray();
        if (membership.Supervisor.IsNull && members.Count > 1)
        {
            if (successor.IsNull && deletingAgent)
                successor = reports.OrderBy(item => item.Id).FirstOrDefault();
            if (successor.IsNull || successor == agent || !members.Any(item => item.Agent == successor))
                throw new InvalidOperationException("Removing a network root requires a member successor.");

            ref var successorMembership = ref GetMembershipReference(successor, network);
            if (successorMembership.Supervisor != agent)
                throw new InvalidOperationException("The root successor must be a direct report.");
            successorMembership.Supervisor = default;
            foreach (var report in reports)
                if (report != successor) GetMembershipReference(report, network).Supervisor = successor;
        }
        else
        {
            foreach (var report in reports) GetMembershipReference(report, network).Supervisor = membership.Supervisor;
        }
        agent.RemoveRelation<AgentNetworkMembership>(network);
        InvalidateNetworkMembers(network, agent);
        NotifyLodNetworkMembers(network, members.Select(item => item.Agent).Append(agent).ToArray());
    }

    private void ValidateSupervisor(Entity agent, Entity network, NetworkTypeDefinition type, Entity supervisor, bool adding)
    {
        if (type.HierarchyMode == NetworkHierarchyMode.Flat)
        {
            if (!supervisor.IsNull) throw new InvalidOperationException("Flat networks cannot assign supervisors.");
            return;
        }

        var members = GetNetworkMembers(network);
        if (supervisor.IsNull)
        {
            var anotherRootExists = members.Any(item => item.Agent != agent && item.Membership.Supervisor.IsNull);
            if (anotherRootExists) throw new InvalidOperationException("A hierarchical network can have only one root.");
            if (adding && members.Count > 0) throw new InvalidOperationException("Every non-root member requires a supervisor.");
            return;
        }
        if (supervisor == agent) throw new InvalidOperationException("An agent cannot supervise itself.");
        RequireAgent(supervisor);
        if (!supervisor.TryGetRelation<AgentNetworkMembership, Entity>(network, out _))
            throw new InvalidOperationException("A supervisor must belong to the same network.");

        // Walk toward the root. Encountering the changed agent would close a cycle.
        var cursor = supervisor;
        var visited = new HashSet<Entity>();
        while (!cursor.IsNull && visited.Add(cursor))
        {
            if (cursor == agent) throw new InvalidOperationException("The supervisor assignment would create a cycle.");
            cursor = GetMembershipReference(cursor, network).Supervisor;
        }
    }

    private NetworkTypeDefinition RequireNetwork(Entity network)
    {
        RequireLiveInStore(network, "network");
        if (!network.TryGetComponent<AgentNetworkData>(out var data))
            throw new ArgumentException("The entity is not an agent network.", nameof(network));
        return _catalog.GetType(data.TypeHash);
    }

    private void RequireAgent(Entity agent)
    {
        RequireLiveInStore(agent, "agent");
        if (!agent.HasComponent<Identity>()) throw new ArgumentException("The entity is not an agent.", nameof(agent));
    }

    private void RequireLiveInStore(Entity entity, string name)
    {
        if (entity.IsNull || entity.Store != _store) throw new ArgumentException($"The {name} must be a live entity in this service's store.", name);
    }

    private void RequireRole(NetworkTypeDefinition type, int roleHash)
    {
        var role = _catalog.GetRole(roleHash);
        if (role.NetworkTypeHash != type.Hash || !type.RoleHashes.Contains(roleHash))
            throw new InvalidOperationException($"Role '{role.Id}' does not belong to network type '{type.Id}'.");
    }

    private static ref AgentNetworkMembership GetMembershipReference(Entity agent, Entity network)
    {
        if (!agent.TryGetRelation<AgentNetworkMembership, Entity>(network, out _))
            throw new KeyNotFoundException("The agent does not belong to the network.");
        return ref agent.GetRelation<AgentNetworkMembership, Entity>(network);
    }

    private void RemoveAllNetworkMemberships(Entity network)
    {
        var agents = new List<Entity>();
        foreach (var link in network.GetIncomingLinks<AgentNetworkMembership>()) agents.Add(link.Entity);
        foreach (var agent in agents) agent.RemoveRelation<AgentNetworkMembership>(network);
        InvalidateAgents(agents);
        NotifyLodNetworkMembers(network, agents.ToArray());
    }

    // Network membership, role, and hierarchy mutations can change both target
    // availability and an active mutual relation for every member in the group.
    private static void InvalidateNetworkMembers(Entity network, params ReadOnlySpan<Entity> additionalAgents)
    {
        var affected = new HashSet<Entity>();
        foreach (var agent in additionalAgents) affected.Add(agent);
        foreach (var link in network.GetIncomingLinks<AgentNetworkMembership>()) affected.Add(link.Entity);
        InvalidateAgents(affected);
    }

    private static void InvalidateAgents(IEnumerable<Entity> agents)
    {
        foreach (var agent in agents)
        {
            if (agent.IsNull || !agent.TryGetComponent<DecisionState>(out _)) continue;
            ref var decision = ref agent.GetComponent<DecisionState>();
            DecisionInvalidation.SignalTargetAvailability(ref decision);
        }
    }

    private void NotifyLodNetworkMembers(Entity network, params ReadOnlySpan<Entity> additionalAgents)
    {
        if (_lodService is null) return;
        var affected = new HashSet<Entity>();
        foreach (var entity in additionalAgents)
        {
            if (!entity.IsNull) affected.Add(entity);
        }
        if (!network.IsNull)
            foreach (var link in network.GetIncomingLinks<AgentNetworkMembership>()) affected.Add(link.Entity);
        _lodService.NotifyNetworkMutation(affected.ToArray());
    }

    private void HandleEntityDelete(EntityDelete deletion)
    {
        if (_handlingDeletion) return;
        _handlingDeletion = true;
        try
        {
            var entity = deletion.Entity;
            if (entity.HasComponent<AgentNetworkData>())
            {
                RemoveAllNetworkMemberships(entity);
                return;
            }
            if (!entity.HasComponent<Identity>()) return;

            var memberships = GetMemberships(entity).ToArray();
            foreach (var membership in memberships)
            {
                var type = RequireNetwork(membership.Network);
                RemoveMembershipCore(entity, membership.Network, type, membership, default, deletingAgent: true);
            }
        }
        finally
        {
            _handlingDeletion = false;
        }
    }
}
