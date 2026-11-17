// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Operations.Elevation;

namespace VibeSalamander.Elevated;

/// <summary>The administrator helper: everything it does is in <see cref="ElevationLauncher.RunHelper"/>.</summary>
internal static class Program
{
    private static int Main(string[] args) => ElevationLauncher.RunHelper(args);
}
