using Jellyfin.Plugin.SceneXray.Vision;

namespace Jellyfin.Plugin.SceneXray.Voice;

/// <summary>Average-linkage agglomerative clustering of voiceprints (cosine similarity).</summary>
public static class VoiceClustering
{
    /// <summary>Returns clusters as lists of indexes into <paramref name="items"/>.</summary>
    public static List<List<int>> Cluster(IReadOnlyList<float[]> items, float threshold)
    {
        int n = items.Count;
        var sim = new float[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                sim[i, j] = sim[j, i] = Vec.Dot(items[i], items[j]);
            }
        }

        var members = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToList();
        var alive = Enumerable.Repeat(true, n).ToArray();
        var best = new int[n];

        // Each cluster's nearest neighbour, so a merge step is O(n) instead of O(n^2).
        void Refresh(int i)
        {
            best[i] = -1;
            for (int j = 0; j < n; j++)
            {
                if (j != i && alive[j] && (best[i] < 0 || sim[i, j] > sim[i, best[i]]))
                {
                    best[i] = j;
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            Refresh(i);
        }

        while (true)
        {
            int bi = -1;
            for (int i = 0; i < n; i++)
            {
                if (alive[i] && best[i] >= 0 && (bi < 0 || sim[i, best[i]] > sim[bi, best[bi]]))
                {
                    bi = i;
                }
            }

            if (bi < 0 || sim[bi, best[bi]] < threshold)
            {
                break;
            }

            int bj = best[bi];
            int a = members[bi].Count, b = members[bj].Count;
            alive[bj] = false;
            members[bi].AddRange(members[bj]);
            for (int k = 0; k < n; k++)
            {
                if (alive[k] && k != bi)
                {
                    sim[bi, k] = sim[k, bi] = ((sim[bi, k] * a) + (sim[bj, k] * b)) / (a + b);
                }
            }

            for (int k = 0; k < n; k++)
            {
                if (!alive[k])
                {
                    continue;
                }

                if (k == bi || best[k] == bi || best[k] == bj)
                {
                    Refresh(k);
                }
                else if (sim[k, bi] > sim[k, best[k]])
                {
                    best[k] = bi;
                }
            }
        }

        return members.Where((_, i) => alive[i]).ToList();
    }
}
