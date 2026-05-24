using System.Text;
using Hoardr.Core.Auth;
using Hoardr.Tests.Support;

namespace Hoardr.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_Then_Verify_Roundtrips()
    {
        var hash = PasswordHasher.Hash("s3cret");
        Assert.True(PasswordHasher.Verify("s3cret", hash));
        Assert.False(PasswordHasher.Verify("wrong", hash));
    }

    [Fact]
    public void Hash_Is_Salted_So_Same_Password_Differs()
    {
        Assert.NotEqual(PasswordHasher.Hash("same"), PasswordHasher.Hash("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2.only.three")]
    public void Verify_Handles_Malformed_Hash(string hash)
    {
        Assert.False(PasswordHasher.Verify("x", hash));
    }
}

public class BasicAuthCredentialsTests
{
    private static string Header(string user, string pass)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

    [Fact]
    public void Parses_Username_And_Password()
    {
        Assert.True(BasicAuthCredentials.TryParse(Header("alice", "pw"), out var c));
        Assert.Equal("alice", c.Username);
        Assert.Equal("pw", c.Password);
    }

    [Fact]
    public void Password_May_Contain_Colon()
    {
        Assert.True(BasicAuthCredentials.TryParse(Header("alice", "a:b:c"), out var c));
        Assert.Equal("a:b:c", c.Password);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer xyz")]
    [InlineData("Basic !!!notbase64")]
    public void Rejects_Invalid_Headers(string? header)
    {
        Assert.False(BasicAuthCredentials.TryParse(header, out _));
    }
}

public class AccountServiceTests
{
    [Fact]
    public void Create_And_Lookup_Account()
    {
        using var db = new TestDatabase();
        var svc = new AccountService(db.Db);

        var created = svc.CreateAccount("alice", "pw");

        Assert.NotNull(created);
        Assert.Equal("alice", svc.GetByName("alice")!.Name);
        Assert.Equal(created!.Id, svc.GetById(created.Id)!.Id);
    }

    [Fact]
    public void Duplicate_Account_Name_Is_Rejected()
    {
        using var db = new TestDatabase();
        var svc = new AccountService(db.Db);
        svc.CreateAccount("alice", "pw");

        Assert.Null(svc.CreateAccount("alice", "other"));
    }

    [Fact]
    public void VerifyCredentials_Checks_Password()
    {
        using var db = new TestDatabase();
        var svc = new AccountService(db.Db);
        svc.CreateAccount("alice", "pw");

        Assert.NotNull(svc.VerifyCredentials("alice", "pw"));
        Assert.Null(svc.VerifyCredentials("alice", "nope"));
        Assert.Null(svc.VerifyCredentials("ghost", "pw"));
    }

    [Fact]
    public void Permissions_Default_To_None_And_Can_Be_Set()
    {
        using var db = new TestDatabase();
        var svc = new AccountService(db.Db);
        var alice = svc.CreateAccount("alice", "pw")!;

        Assert.False(svc.CanPull(alice.Id, "app"));
        Assert.False(svc.CanPush(alice.Id, "app"));

        svc.SetPermission(alice.Id, "app", canPull: true, canPush: false);
        Assert.True(svc.CanPull(alice.Id, "app"));
        Assert.False(svc.CanPush(alice.Id, "app"));

        svc.SetPermission(alice.Id, "app", canPull: true, canPush: true);
        Assert.True(svc.CanPush(alice.Id, "app"));
        Assert.Single(svc.ListPermissions(alice.Id)); // updated, not duplicated
    }

    [Fact]
    public void Delete_Permission_Defaults_Off_And_Is_Settable()
    {
        using var db = new TestDatabase();
        var svc = new AccountService(db.Db);
        var alice = svc.CreateAccount("alice", "pw")!;

        svc.SetPermission(alice.Id, "app", canPull: true, canPush: true);
        Assert.False(svc.CanDelete(alice.Id, "app"));

        svc.SetPermission(alice.Id, "app", canPull: true, canPush: true, canDelete: true);
        Assert.True(svc.CanDelete(alice.Id, "app"));
        Assert.Single(svc.ListPermissions(alice.Id)); // still one row
    }

    [Fact]
    public void CanCreate_Defaults_Off_And_Is_Settable()
    {
        using var db = new TestDatabase();
        var svc = new AccountService(db.Db);
        var ci = svc.CreateAccount("ci", "pw")!;

        Assert.False(svc.GetCanCreate(ci.Id));
        svc.SetCanCreate(ci.Id, true);
        Assert.True(svc.GetCanCreate(ci.Id));

        Assert.False(svc.AnyPermissionForRepo("team/app"));
        svc.SetPermission(ci.Id, "team/app", canPull: true, canPush: true);
        Assert.True(svc.AnyPermissionForRepo("team/app"));
    }

    [Fact]
    public void DeleteAccount_Removes_Account_And_Permissions()
    {
        using var db = new TestDatabase();
        var svc = new AccountService(db.Db);
        var alice = svc.CreateAccount("alice", "pw")!;
        svc.SetPermission(alice.Id, "app", true, true);

        Assert.True(svc.DeleteAccount(alice.Id));
        Assert.Null(svc.GetById(alice.Id));
        Assert.Empty(svc.ListPermissions(alice.Id));
    }
}

public class AuthenticatorTests
{
    private static string Header(string user, string pass)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

    [Fact]
    public void Master_Token_Authenticates_As_Master()
    {
        using var db = new TestDatabase();
        var auth = new Authenticator(new AccountService(db.Db), masterToken: "TOKEN123");

        var id = auth.Authenticate(Header("anything", "TOKEN123"));

        Assert.NotNull(id);
        Assert.True(id!.IsMaster);
        Assert.True(auth.CanPull(id, "any-repo"));
        Assert.True(auth.CanPush(id, "any-repo"));
    }

    [Fact]
    public void Account_Authenticates_And_Honors_Permissions()
    {
        using var db = new TestDatabase();
        var accounts = new AccountService(db.Db);
        var alice = accounts.CreateAccount("alice", "pw")!;
        accounts.SetPermission(alice.Id, "app", canPull: true, canPush: false);
        var auth = new Authenticator(accounts, masterToken: "TOKEN123");

        var id = auth.Authenticate(Header("alice", "pw"));

        Assert.NotNull(id);
        Assert.False(id!.IsMaster);
        Assert.True(auth.CanPull(id, "app"));
        Assert.False(auth.CanPush(id, "app"));
        Assert.False(auth.CanPull(id, "other"));
    }

    [Fact]
    public void Wrong_Password_And_Unknown_User_Fail()
    {
        using var db = new TestDatabase();
        var accounts = new AccountService(db.Db);
        accounts.CreateAccount("alice", "pw");
        var auth = new Authenticator(accounts, masterToken: "TOKEN123");

        Assert.Null(auth.Authenticate(Header("alice", "wrong")));
        Assert.Null(auth.Authenticate(Header("ghost", "pw")));
        Assert.Null(auth.Authenticate(null));
    }
}
