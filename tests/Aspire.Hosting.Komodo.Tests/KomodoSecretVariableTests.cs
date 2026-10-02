using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Komodo;
using Xunit;

namespace Aspire.Hosting.Komodo.Tests;

/// <summary>Covers the names of the Komodo Variables a stack's secrets are vaulted in, which destroy deletes.</summary>
public class KomodoSecretVariableTests
{
    [Fact]
    public void NamesEachSecretParameterUnderItsStack()
    {
        var model = new DistributedApplicationModel(new IResource[]
        {
            new ParameterResource("db-password", _ => "x", secret: true),
            new ParameterResource("authSecret", _ => "x", secret: true),
            new ParameterResource("region", _ => "x"),
        });

        Assert.Equal(
            ["app-pr-7_DB_PASSWORD", "app-pr-7_AUTHSECRET"],
            KomodoSteps.SecretKeys(model).Select(k => $"app-pr-7_{k}"));
        Assert.Equal(
            ["app_pr_7_db_password", "app_pr_7_authsecret"],
            KomodoSteps.SecretKeys(model).Select(k => KomodoSteps.SecretVariableName("app-pr-7", k)));
    }

    [Fact]
    public void TargetsOnlyTheStacksOwnSecretsNotAnotherStackSharingItsPrefix()
    {
        // Stack `app` has secret DB; stack `app-x` vaults `app_x_secret`. A prefix match on `app_` would take it.
        Assert.Equal("app_db", KomodoSteps.SecretVariableName("app", "DB"));
        Assert.NotEqual("app_x_secret", KomodoSteps.SecretVariableName("app", "DB"));
    }
}
