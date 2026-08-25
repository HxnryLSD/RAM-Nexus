using System.Collections.ObjectModel;
using RAM.Core.Infrastructure;
using RAM.Core.Models;

namespace RAM;

/// <summary>
/// Single owner of the in-memory account list and its persistence policy. Every mutation
/// is "save first, then surface": if the store refuses (a password-locked vault without a
/// session password), the in-memory list is left untouched and the caller reports it —
/// instead of each page rolling back ad-hoc. <see cref="Accounts"/> is the observable list
/// the UI binds to; it mirrors the on-disk vault.
/// </summary>
public sealed class AccountRepository
{
    private readonly AccountStore _store;

    public AccountRepository(AccountStore store) => _store = store;

    /// <summary>The in-memory account list the UI binds to.</summary>
    public ObservableCollection<Account> Accounts { get; } = new();

    /// <summary>Load the vault into <see cref="Accounts"/> (startup / after unlock).</summary>
    public void Load(string? password = null)
    {
        foreach (var account in _store.Load(password))
            Accounts.Add(account);
    }

    /// <summary>Persist the current list. False when the store refused (vault locked).</summary>
    public bool Save(string? password = null, bool bypassCountCheck = false)
        => _store.Save(Accounts.ToList(), password, bypassCountCheck);

    /// <summary>Add accounts: save first, surface only on success.</summary>
    public bool Add(IEnumerable<Account> accounts)
    {
        var combined = Accounts.Concat(accounts).ToList();
        if (!_store.Save(combined)) return false;
        foreach (var account in accounts)
            Accounts.Add(account);
        return true;
    }

    /// <summary>Remove an account: save first, surface only on success.</summary>
    public bool Remove(Account account)
    {
        var remaining = Accounts.Where(a => !ReferenceEquals(a, account)).ToList();
        if (!_store.Save(remaining, bypassCountCheck: true)) return false;
        Accounts.Remove(account);
        return true;
    }

    /// <summary>
    /// Apply a vault-field edit to an account, persisting first and rolling the account
    /// back if the store refuses. Re-throws <see cref="ArgumentOutOfRangeException"/>
    /// (the field length caps) for the caller to report.
    /// </summary>
    public bool Update(Account account, Action apply)
    {
        var previous = (account.Alias, account.Group, account.Description, account.Password);
        apply(); // may throw ArgumentOutOfRangeException (length caps)
        if (!_store.Save(Accounts.ToList(), bypassCountCheck: true))
        {
            (account.Alias, account.Group, account.Description, account.Password) = previous;
            return false;
        }
        return true;
    }

    /// <summary>Save a (possibly merged) list, then replace <see cref="Accounts"/> with it.</summary>
    public bool SaveAndReplace(IEnumerable<Account> accounts)
    {
        if (!_store.Save(accounts.ToList(), bypassCountCheck: true)) return false;
        Accounts.Clear();
        foreach (var account in accounts)
            Accounts.Add(account);
        return true;
    }
}
