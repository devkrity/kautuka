# Network access matching

Network access defaults to deny. A request has a destination and, when known, a user, agent and connector. Connector catalogue availability does not affect network decisions.

Rules can target the whole platform, one user, a user and agent, or a user and connector. Only administrators manage platform rules. Users with the privilege to manage their own network access can manage their own user, user-and-agent and user-and-connector rules; administrators can manage them too. There is no connector-across-users rule.

Approval cards identify the agent and connector by name when available. Their choices distinguish allowing the agent, allowing the connector across the user's activity, and allowing all of the user's activity. The underlying connector ID stays the same for rule matching.

When a user's management privilege is removed, their own rules pause until it is restored. Rules created by an administrator remain active.

Manager checks every rule whose target and destination match. An explicit block wins over every applicable allow; otherwise an applicable allow permits the new connection. No matching allow means deny. Emergency brakes and hard deny-all remain above all rules. Existing network and LAN restrictions continue to apply.

Discovery during connector save has a user and connector but no agent. It checks platform, user and user-and-connector rules; an absent agent grants no access. An unapproved connection produces a normal approval request for the user and connector. The connector can be saved while approval is pending, and Test connection can retry discovery.

Existing pre-release network rules and saved NAC privilege records are reset when this model is introduced; user accounts and brakes are not reset.

## Examples

Assume each example concerns the same host and port, no brake or hard deny-all applies, and no other rule matches.

1. An administrator allows the host for the platform: Alice's agent and connector may connect.
2. Alice allows the host for herself: Alice's agents and connectors may connect; Bob receives no access from that rule.
3. Alice allows the host for her Secretary agent: Secretary may connect; her other agents receive no access from that rule.
4. Alice allows the host for Web Tools: her Web Tools may connect during Save and when used by Secretary; Bob's Web Tools receives no access from that rule.
5. The platform allows the host and Alice blocks it: Alice's connection is blocked.
6. Alice allows Web Tools but blocks the same host for Secretary: Secretary using Web Tools is blocked; a different agent using Alice's Web Tools is allowed.

Skill scripts run in the agent sandbox and use the platform, user and user-and-agent rules. Individual skills are not separately attributed.
