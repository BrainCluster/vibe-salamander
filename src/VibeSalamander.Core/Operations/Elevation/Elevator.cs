// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using VibeSalamander.Core.Localization;

namespace VibeSalamander.Core.Operations.Elevation;

/// <summary>
/// One operation's way to administrator rights, for the operations outside the copy engine (rename, new folder,
/// attributes, case): changes go through <see cref="Ops"/>, this process's until the user chose "Continue as
/// administrator" for a denied change, the helper's after that (one Windows prompt for the operation). Disposing it
/// ends the helper.
/// </summary>
public sealed class Elevator : IDisposable
{
    private readonly IElevation? _elevation;
    private bool _refused;

    /// <param name="elevation">The helper's launcher; null: never offered (no helper beside the program).</param>
    public Elevator(IElevation? elevation)
    {
        _elevation = elevation;
    }

    /// <summary>The changes this process makes itself (tests put in one that is denied access).</summary>
    internal IFileOps Local { get; init; } = LocalFileOps.Instance;

    /// <summary>This process already runs as administrator (then there is nothing more to ask for).</summary>
    internal Func<bool> IsElevated { get; init; } = () => ElevationRules.IsElevated;

    /// <summary>The helper, once the user said yes and Windows started it.</summary>
    public IElevatedSession? Session { get; private set; }

    /// <summary>Where the changes go now: the helper once it runs, this process before.</summary>
    public IFileOps Ops => (IFileOps?)Session ?? Local;

    /// <summary>The failure was "access denied" and continuing as administrator can be offered for it.</summary>
    public bool CanOffer(Exception error) =>
        Session == null && _elevation != null && !_refused && ElevationRules.IsAccessDenied(error) && !IsElevated();

    /// <summary>
    /// Starts the helper (Windows asks). Null when it runs; otherwise what to tell the user: empty when the user said
    /// no to Windows, the reason when it couldn't start. Not offered again in this operation after either.
    /// </summary>
    public async Task<string?> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_elevation == null)
            return string.Empty;
        try
        {
            Session = await _elevation.StartAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (ElevationRefusedException)
        {
            _refused = true;
            return string.Empty;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            _refused = true;
            return L.F("Couldn't continue as administrator: {0}", ex.Message);
        }
    }

    /// <summary>
    /// The error question for a failed change, offering administrator rights for "access denied" (once).
    /// <see cref="ErrorChoice.Elevate"/> back: do the change again through <see cref="Ops"/> (the helper) — after the
    /// user's yes, and without asking for every later denied change. <paramref name="asAdministrator"/>: the change
    /// already ran as administrator (and was denied all the same): an ordinary question.
    /// </summary>
    public async Task<ErrorChoice> AskAsync(Func<OperationError, Task<ErrorChoice>> ask, string path, string message, Exception error,
        bool asAdministrator, CancellationToken cancellationToken = default)
    {
        if (!asAdministrator && ElevationRules.IsAccessDenied(error))
        {
            if (Session != null)
                return ErrorChoice.Elevate;
            if (CanOffer(error))
            {
                ErrorChoice choice = await ask(new OperationError(path, message, CanElevate: true)).ConfigureAwait(false);
                if (choice != ErrorChoice.Elevate)
                    return choice;
                string? problem = await StartAsync(cancellationToken).ConfigureAwait(false);
                if (problem == null)
                    return ErrorChoice.Elevate;
                if (problem.Length > 0)
                    message += "\n" + problem;
            }
        }
        return await ask(new OperationError(path, message)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Session?.Dispose();
        Session = null;
    }
}
