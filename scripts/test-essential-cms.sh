#!/usr/bin/env bash
# Deployment gate for the production CMS surface. The repository also carries
# broad legacy/mobile integration packs; those are not activated by this lean
# native gateway deployment and are intentionally outside this gate.
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
DOTNET_BIN="${DOTNET_BIN:-dotnet}"
PROJECT="$ROOT/tests/JeebGateway.IntegrationTests/JeebGateway.IntegrationTests.csproj"

filter=(
  AdminAuthSecurityTests
  AdminAuthenticationCeremonyTests
  AdminOidcFlowTests
  AdminOpenApiContractTests
  AdminEssentialCapabilityTests
  ControllerHeaderSpoofScopingTests
  StatelessGatewayEnforcementTests
  CodOwnerBoundaryGuardTests
  StateServiceRewireTests
  StateServiceRefreshTokenStoreTests
  GenericCaseGatewayServiceTests
  GenericCaseClientContractTests
  CaseRouteContractTests
  CaseIdempotencyMiddlewareTests
  CaseEventCallbackContractTests
  CaseEvidenceCollectorContractTests
  DisputeSupportEndpointContractTests
  AdminDeliveryEvidenceSecurityTests
  AdminSettlementSecurityTests
  AuthoritativeCodCompositionTests
  UpgSettlementOwnerContractTests
  SettlementEventCallbackContractTests
)

expression=""
for test_class in "${filter[@]}"; do
  [ -z "$expression" ] || expression+="|"
  expression+="FullyQualifiedName~$test_class"
done

"$DOTNET_BIN" test "$PROJECT" --configuration Release --nologo --filter "$expression"
