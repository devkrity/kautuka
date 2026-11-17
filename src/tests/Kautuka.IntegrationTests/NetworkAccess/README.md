# Network access test index

The `U`, `C`, `G` and `V7` prefixes are historical plan and review IDs. They are labels, not evidence that every platform or production traffic path has been tested. Test names and their setup describe the current coverage.

| IDs | Test file | Main coverage |
| --- | --- | --- |
| U1 | [U1_DecisionFunctionTests.cs](U1_DecisionFunctionTests.cs) | Destination matching, decision order and rule scopes |
| U2 | [U2_PolicyStoreTests.cs](U2_PolicyStoreTests.cs) | Policy persistence, pending requests, limits and revisions |
| U3 | [U3_ProtoCompatibilityTests.cs](U3_ProtoCompatibilityTests.cs) | External authorization protocol field numbers |
| U5–U7, C6, C7, C10 | [U5_U6_U7_C6_C7_C10_ContainerTests.cs](U5_U6_U7_C6_C7_C10_ContainerTests.cs) | Container specs, renderers, address allocation, ruleset hashes and topology checks |
| U8 | [U8_NonPublicV4AndOpenRulesTests.cs](U8_NonPublicV4AndOpenRulesTests.cs) | Non-public IPv4 handling and ruleset golden files |
| C2–C5, C14, C16 | [C2_C3_C4_C5_C14_C16_ListenerAndHostTests.cs](C2_C3_C4_C5_C14_C16_ListenerAndHostTests.cs) | Authorization listener, sockets, Windows TLS and service isolation |
| C11–C13, C15, C17–C23 | [C11_To_C21_ControllerIntegrationTests.cs](C11_To_C21_ControllerIntegrationTests.cs) | Policy changes, scopes, approvals and brakes |
| G2–G7 | [G2_To_G7_GatewayAndChannelHostTests.cs](G2_To_G7_GatewayAndChannelHostTests.cs) | Gateway, remote MCP and ChannelHost network checks |
| V7-4, V7-12 | [V7_4_And_V7_12_GatewayPrivilegeSyncTests.cs](V7_4_And_V7_12_GatewayPrivilegeSyncTests.cs) | Gateway user privileges and synchronization |
| V7-13 | [V7_13_GatewayNetworkDecisionAuthorizationHttpTests.cs](V7_13_GatewayNetworkDecisionAuthorizationHttpTests.cs) | Network access HTTP endpoints and error handling |
| V7-20 | [V7_20_AcceptanceHarnessContractTests.cs](V7_20_AcceptanceHarnessContractTests.cs) | Acceptance source wiring; this is not a live traffic test |
| V7-41 | [V7_41_ConnectorDiagnosticsTests.cs](V7_41_ConnectorDiagnosticsTests.cs) | Connector diagnostics |
| V7-55 | [V7_55_RestoredWorkloadTests.cs](V7_55_RestoredWorkloadTests.cs) and the other `V7_55_*` files in this directory | Restart adoption, quarantine and reconciliation; the [live restart test](V7_55_LiveQuarantineRestartTests.cs) is opt-in and requires a disposable rootless Podman store |

Other files in this directory have descriptive names and cover additional behavior. The [acceptance harness](../../Kautuka.NetworkAccess.Acceptance/Program.cs) exercises a live topology when its prerequisites are available; its source-contract tests do not substitute for a live run.
