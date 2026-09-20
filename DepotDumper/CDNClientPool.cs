using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2.CDN;
namespace DepotDumper
{
    class CDNClientPool
    {
        private const int ServerEndpointMinimumSize = 8;
        private readonly Steam3Session steamSession;
        private readonly uint appId;
        public Client CDNClient { get; }
        public Server ProxyServer { get; private set; }
        // Servers that recently answered 503/502/504/429 during this run: new pools list them last for a few minutes
        private static readonly ConcurrentDictionary<string, DateTime> recentlyBad = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        public static void MarkBad(string host) { if (!string.IsNullOrEmpty(host)) recentlyBad[host] = DateTime.UtcNow; }
        private static bool IsRecentlyBad(string host) => recentlyBad.TryGetValue(host, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(5);
        private readonly ConcurrentStack<Server> activeConnectionPool = [];
        private readonly BlockingCollection<Server> availableServerEndpoints = [];
        private readonly AutoResetEvent populatePoolEvent = new(true);
        private readonly Task monitorTask;
        private readonly CancellationTokenSource shutdownToken = new();
        public CancellationTokenSource ExhaustedToken { get; set; }
        public CDNClientPool(Steam3Session steamSession, uint appId)
        {
            this.steamSession = steamSession;
            this.appId = appId;
            CDNClient = new Client(steamSession.steamClient);
            monitorTask = Task.Factory.StartNew(ConnectionPoolMonitorAsync).Unwrap();
        }
        public void Shutdown()
        {
            shutdownToken.Cancel();
            monitorTask.Wait();
        }
        private async Task<IReadOnlyCollection<Server>> FetchBootstrapServerListAsync()
        {
            try
            {
                var cdnServers = await this.steamSession.steamContent.GetServersForSteamPipe();
                if (cdnServers != null)
                {
                    return cdnServers;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to retrieve content server list: {0}", ex.Message);
            }
            return null;
        }
        private async Task ConnectionPoolMonitorAsync()
        {
            var didPopulate = false;
            while (!shutdownToken.IsCancellationRequested)
            {
                populatePoolEvent.WaitOne(TimeSpan.FromSeconds(1));
                if (availableServerEndpoints.Count < ServerEndpointMinimumSize && steamSession.steamClient.IsConnected)
                {
                    var servers = await FetchBootstrapServerListAsync().ConfigureAwait(false);
                    if (servers == null || servers.Count == 0)
                    {
                        ExhaustedToken?.Cancel();
                        return;
                    }
                    ProxyServer = servers.Where(x => x.UseAsProxy).FirstOrDefault();
                    var weightedCdnServers = servers
                        .Where(server =>
                        {
                            var isEligibleForApp = server.AllowedAppIds.Length == 0 || server.AllowedAppIds.Contains(appId);
                            return isEligibleForApp && (server.Type == "SteamCache" || server.Type == "CDN");
                        })
                        .Select(server =>
                        {
                            AccountSettingsStore.Instance.ContentServerPenalty.TryGetValue(server.Host, out var penalty);
                            return (server, penalty);
                        })
                        .OrderBy(pair => pair.penalty + (IsRecentlyBad(pair.server.Host) ? 1000 : 0)).ThenBy(pair => pair.server.WeightedLoad);
                    foreach (var (server, weight) in weightedCdnServers)
                    {
                        for (var i = 0; i < server.NumEntries; i++)
                        {
                            availableServerEndpoints.Add(server);
                        }
                    }
                    didPopulate = true;
                }
                else if (availableServerEndpoints.Count == 0 && !steamSession.steamClient.IsConnected && didPopulate)
                {
                    ExhaustedToken?.Cancel();
                    return;
                }
            }
        }
        private Server BuildConnection(CancellationToken token)
        {
            if (availableServerEndpoints.Count < ServerEndpointMinimumSize)
            {
                populatePoolEvent.Set();
            }
            return availableServerEndpoints.Take(token);
        }
        public Server GetConnection(CancellationToken token)
        {
            if (!activeConnectionPool.TryPop(out var connection))
            {
                connection = BuildConnection(token);
            }
            return connection;
        }
        public void ReturnConnection(Server server)
        {
            if (server == null) return;
            activeConnectionPool.Push(server);
        }
        public void ReturnBrokenConnection(Server server)
        {
            if (server == null) return;
        }
    }
}
