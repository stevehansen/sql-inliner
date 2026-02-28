using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using SqlInliner.Optimize;

namespace SqlInliner.Tests;

public class CredentialStoreTests
{
    #region MockCredentialStore

    private sealed class MockCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, StoredCredential> _store = new();

        public void Store(string key, string username, string password)
        {
            _store[key] = new StoredCredential(username, password);
        }

        public StoredCredential? Retrieve(string key)
        {
            return _store.TryGetValue(key, out var cred) ? cred : null;
        }

        public bool Remove(string key)
        {
            return _store.Remove(key);
        }

        public IReadOnlyList<(string Key, string Username)> List()
        {
            return _store.Select(kv => (kv.Key, kv.Value.Username)).ToList();
        }
    }

    #endregion

    #region BuildKey tests

    [Test]
    public void BuildKey_NormalizesLowercase()
    {
        var key = CredentialStoreFactory.BuildKey("MyServer", "MyDatabase");
        key.ShouldBe(@"myserver\mydatabase");
    }

    [Test]
    public void BuildKey_TrimsWhitespace()
    {
        var key = CredentialStoreFactory.BuildKey("  server  ", "  db  ");
        key.ShouldBe(@"server\db");
    }

    [Test]
    public void BuildKey_HandlesInstanceName()
    {
        var key = CredentialStoreFactory.BuildKey("server\\instance", "db");
        key.ShouldBe(@"server\instance\db");
    }

    #endregion

    #region MockCredentialStore round-trip tests

    [Test]
    public void MockStore_StoreAndRetrieve()
    {
        var store = new MockCredentialStore();
        store.Store("key1", "user1", "pass1");

        var cred = store.Retrieve("key1");
        cred.ShouldNotBeNull();
        cred.Username.ShouldBe("user1");
        cred.Password.ShouldBe("pass1");
    }

    [Test]
    public void MockStore_RetrieveNonExistent_ReturnsNull()
    {
        var store = new MockCredentialStore();
        store.Retrieve("nonexistent").ShouldBeNull();
    }

    [Test]
    public void MockStore_Remove()
    {
        var store = new MockCredentialStore();
        store.Store("key1", "user1", "pass1");

        store.Remove("key1").ShouldBeTrue();
        store.Retrieve("key1").ShouldBeNull();
    }

    [Test]
    public void MockStore_RemoveNonExistent_ReturnsFalse()
    {
        var store = new MockCredentialStore();
        store.Remove("nonexistent").ShouldBeFalse();
    }

    [Test]
    public void MockStore_List()
    {
        var store = new MockCredentialStore();
        store.Store("key1", "user1", "pass1");
        store.Store("key2", "user2", "pass2");

        var list = store.List();
        list.Count.ShouldBe(2);
        list.ShouldContain(e => e.Key == "key1" && e.Username == "user1");
        list.ShouldContain(e => e.Key == "key2" && e.Username == "user2");
    }

    [Test]
    public void MockStore_Overwrite()
    {
        var store = new MockCredentialStore();
        store.Store("key1", "user1", "pass1");
        store.Store("key1", "user2", "pass2");

        var cred = store.Retrieve("key1");
        cred.ShouldNotBeNull();
        cred.Username.ShouldBe("user2");
        cred.Password.ShouldBe("pass2");

        store.List().Count.ShouldBe(1);
    }

    #endregion

    #region ConnectionStringHelper.Resolve tests

    [Test]
    public void Resolve_WithExistingCredentials_NoChange()
    {
        var cs = "Server=myserver;Database=mydb;User Id=myuser;Password=mypass";
        var result = ConnectionStringHelper.Resolve(cs, new MockCredentialStore());

        result.ShouldContain("User Id=myuser");
        result.ShouldContain("Password=mypass");
    }

    [Test]
    public void Resolve_WithIntegratedSecurity_NoChange()
    {
        var cs = "Server=myserver;Database=mydb;Integrated Security=true";
        var result = ConnectionStringHelper.Resolve(cs, new MockCredentialStore());

        result.ShouldContain("Integrated Security=True");
        result.ShouldNotContain("User Id=");
    }

    [Test]
    public void Resolve_InjectsStoredCredentials()
    {
        var store = new MockCredentialStore();
        var key = CredentialStoreFactory.BuildKey("myserver", "mydb");
        store.Store(key, "storeduser", "storedpass");

        var cs = "Server=myserver;Database=mydb";
        var result = ConnectionStringHelper.Resolve(cs, store);

        result.ShouldContain("User Id=storeduser");
        result.ShouldContain("Password=storedpass");
    }

    [Test]
    public void Resolve_FallsBackToIntegratedSecurity()
    {
        var cs = "Server=myserver;Database=mydb";
        var result = ConnectionStringHelper.Resolve(cs, new MockCredentialStore());

        result.ShouldContain("Integrated Security=True");
    }

    [Test]
    public void Resolve_SetsApplicationName()
    {
        var cs = "Server=myserver;Database=mydb;Integrated Security=true";
        var result = ConnectionStringHelper.Resolve(cs, null);

        // Verify via SqlConnectionStringBuilder that ApplicationName was set
        var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(result);
        csb.ApplicationName.ShouldNotBe(".Net SqlClient Data Provider");
        csb.ApplicationName.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public void Resolve_PreservesExistingApplicationName()
    {
        var cs = "Server=myserver;Database=mydb;Integrated Security=true;Application Name=MyApp";
        var result = ConnectionStringHelper.Resolve(cs, null);

        result.ShouldContain("Application Name=MyApp");
    }

    [Test]
    public void Resolve_NullStore_FallsBackToIntegratedSecurity()
    {
        var cs = "Server=myserver;Database=mydb";
        var result = ConnectionStringHelper.Resolve(cs, null);

        result.ShouldContain("Integrated Security=True");
    }

    [Test]
    public void Resolve_NoDatabase_FallsBackToIntegratedSecurity()
    {
        var store = new MockCredentialStore();
        store.Store(CredentialStoreFactory.BuildKey("myserver", "mydb"), "user", "pass");

        var cs = "Server=myserver";
        var result = ConnectionStringHelper.Resolve(cs, store);

        // Can't look up credentials without database, so falls back
        result.ShouldContain("Integrated Security=True");
    }

    [Test]
    public void Resolve_ExistingCredentials_IgnoresStore()
    {
        var store = new MockCredentialStore();
        var key = CredentialStoreFactory.BuildKey("myserver", "mydb");
        store.Store(key, "storeduser", "storedpass");

        var cs = "Server=myserver;Database=mydb;User Id=cliuser;Password=clipass";
        var result = ConnectionStringHelper.Resolve(cs, store);

        // CLI credentials take priority
        result.ShouldContain("User Id=cliuser");
        result.ShouldContain("Password=clipass");
    }

    #endregion

    #region Windows round-trip (platform-specific)

    [Test]
    [Platform("Win")]
    public void WindowsCredentialStore_RoundTrip()
    {
        var store = new WindowsCredentialStore();
        var testKey = $"test-{System.Guid.NewGuid():N}";

        try
        {
            // Store
            store.Store(testKey, "testuser", "testpass123!");

            // Retrieve
            var cred = store.Retrieve(testKey);
            cred.ShouldNotBeNull();
            cred.Username.ShouldBe("testuser");
            cred.Password.ShouldBe("testpass123!");

            // List should contain it
            var list = store.List();
            list.ShouldContain(e => e.Key == testKey && e.Username == "testuser");
        }
        finally
        {
            // Always clean up
            store.Remove(testKey);
        }

        // Verify removal
        store.Retrieve(testKey).ShouldBeNull();
    }

    #endregion
}
