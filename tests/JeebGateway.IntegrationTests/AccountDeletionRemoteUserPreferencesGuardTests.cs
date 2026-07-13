using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-215 (E20) DoD grep-guard — proves the account-deletion soft status-flip persists
/// via remote-user-preferences, NOT user-management (Q-079 / GR-2). Mirrors the
/// <see cref="DeliveryStateMachineRetiredGuardTests"/> source-scan idiom: it asserts on the
/// LIVE (comment-stripped) gateway source so a future revert to a user-management delete, or
/// dropping the remote-user-preferences route, trips the guard.
/// </summary>
public class AccountDeletionRemoteUserPreferencesGuardTests
{
    /// <summary>
    /// The remote-user-preferences-backed account-deletion store exists and actually routes
    /// through the shared remote-user-preferences client under the namespaced key.
    /// </summary>
    [Fact]
    public void RemoteUserPreferences_Backed_AccountDeletion_Store_Exists_And_Routes_Through_Rup()
    {
        var srcRoot = LocateGatewaySrc();
        srcRoot.Should().NotBeNull("the gateway src/JeebGateway tree must be locatable from the test bin dir");

        var storePath = Path.Combine(srcRoot!, "Users", "RemoteUserPreferencesAccountDeletionStore.cs");
        File.Exists(storePath).Should().BeTrue(
            "JEBV4-215: the account-deletion flip must persist via a remote-user-preferences-backed store");

        var storeSrc = File.ReadAllText(storePath);
        storeSrc.Should().Contain("IAccountDeletionStore",
            "the store must implement the account-deletion lifecycle contract");
        storeSrc.Should().Contain("ServiceRemoteUserPreferencesClient",
            "the flip must route through the remote-user-preferences client (NOT user-management)");
        storeSrc.Should().Contain("jeeb.account_deletion",
            "the status blob must be written under the namespaced remote-user-preferences key");
        storeSrc.Should().Contain("Data_UpdatePreferenceAsync",
            "the flip must be written to remote-user-preferences via its preference write API");
    }

    /// <summary>
    /// The delete endpoints flip status through the account-deletion store and NO LONGER
    /// hard-delete through user-management (the DoD "not user-management — grep confirms").
    /// </summary>
    [Fact]
    public void DeleteProfile_Flips_Via_Store_Not_UserManagement()
    {
        var controllerPath = ControllerSourceScan.Locate("UserController.cs");
        controllerPath.Should().NotBeNull("UserController.cs must be locatable");

        var live = ControllerSourceScan.LiveCode(controllerPath!);

        // The soft-delete flip goes through the account-deletion store...
        ControllerSourceScan.Count(live, "_accountDeletion.RequestAsync(").Should().BeGreaterThan(0,
            "the delete path must record the flip via the account-deletion store");

        // ...and NOT through user-management's account delete. The former direct hard-delete
        // (_serviceUserManagementClient.DeleteAsync(<userId>)) must be gone from the live source.
        // (Bulk delete-by-emails uses DeleteByEmailsAsync — a different method — so it is unaffected.)
        ControllerSourceScan.Count(live, ".DeleteAsync(").Should().Be(0,
            "JEBV4-215 DoD: the account-deletion flip must NOT route through user-management (.DeleteAsync)");
    }

    /// <summary>
    /// Program.cs wires the remote-user-preferences-backed store and the scheduled purge worker.
    /// </summary>
    [Fact]
    public void Program_Wires_Rup_Store_And_Purge_Worker()
    {
        var srcRoot = LocateGatewaySrc();
        srcRoot.Should().NotBeNull();

        var program = File.ReadAllText(Path.Combine(srcRoot!, "Program.cs"));
        program.Should().Contain("RemoteUserPreferencesAccountDeletionStore",
            "Program.cs must register the remote-user-preferences-backed account-deletion store");
        program.Should().Contain("AccountDeletionPurgeWorker",
            "Program.cs must schedule the account-deletion purge worker (30-day SLA sweep)");
    }

    private static string? LocateGatewaySrc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "JeebGateway");
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "Program.cs")))
            {
                return candidate;
            }
        }
        return null;
    }
}
