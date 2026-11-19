// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace VibeSalamander.Plugins.Git;

/// <summary>
/// The commit graph as text, one line per commit (newest first, each before its parents): a lane per line of
/// history, "●" the commit, "│" a lane passing by, "╮" a merge's other parent starting a lane, "╯" a lane ending
/// in this commit (a branch that started from it).
/// </summary>
internal static class GitGraph
{
    public static IReadOnlyList<string> Lay(IReadOnlyList<(string Id, IReadOnlyList<string> Parents)> commits)
    {
        var lanes = new List<string?>(); // what each lane waits for (a commit id), or null: free
        var lines = new List<string>(commits.Count);
        foreach ((string id, IReadOnlyList<string> parents) in commits)
        {
            int own = lanes.IndexOf(id);
            if (own < 0)
            {
                own = lanes.IndexOf(null);
                if (own < 0)
                {
                    own = lanes.Count;
                    lanes.Add(null);
                }
            }
            var marks = new char[Math.Max(lanes.Count, own + 1) + parents.Count];
            for (int i = 0; i < marks.Length; i++)
                marks[i] = i < lanes.Count && lanes[i] != null ? '│' : ' ';
            marks[own] = '●';
            // other lanes waiting for this commit end here
            for (int i = 0; i < lanes.Count; i++)
            {
                if (i != own && lanes[i] == id)
                {
                    lanes[i] = null;
                    marks[i] = '╯';
                }
            }
            // the first parent continues this lane; the others go into a lane waiting for them (or a new one)
            lanes[own] = parents.Count > 0 ? parents[0] : null;
            for (int p = 1; p < parents.Count; p++)
            {
                int lane = lanes.IndexOf(parents[p]);
                if (lane >= 0)
                {
                    if (marks[lane] == '│')
                        marks[lane] = '╮'; // joins a lane already there
                    continue;
                }
                lane = lanes.IndexOf(null);
                if (lane < 0)
                {
                    lane = lanes.Count;
                    lanes.Add(null);
                }
                lanes[lane] = parents[p];
                marks[lane] = '╮';
            }
            while (lanes.Count > 0 && lanes[^1] == null)
                lanes.RemoveAt(lanes.Count - 1);
            var line = new StringBuilder();
            foreach (char mark in marks)
                line.Append(mark).Append(' ');
            lines.Add(line.ToString().TrimEnd());
        }
        return lines;
    }
}
