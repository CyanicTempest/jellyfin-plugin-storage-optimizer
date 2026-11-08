// SPDX-License-Identifier: GPL-3.0-only
using Jellyfin.Plugin.DataManager.Encoding;
using Jellyfin.Plugin.DataManager.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DataManager;

/// <summary>
/// Registers the plugin's services with Jellyfin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<LanguageCodes>();
        serviceCollection.AddSingleton<ReportUpdater>();
        serviceCollection.AddSingleton<StorageScanner>();
        serviceCollection.AddSingleton<SummaryBuilder>();
        serviceCollection.AddSingleton<ResolutionAnalyzer>();
        serviceCollection.AddSingleton<HardwareAcceleration>();
        serviceCollection.AddSingleton<GpuDetector>();
        serviceCollection.AddSingleton<ViewerMonitor>();
        serviceCollection.AddSingleton<SpeedEstimator>();
        serviceCollection.AddSingleton<EncodeService>();
        serviceCollection.AddSingleton<GpuTester>();
        serviceCollection.AddSingleton<FrameComparer>();
        serviceCollection.AddSingleton<MediaDeleter>();
        serviceCollection.AddSingleton<LeftoverCleaner>();
        serviceCollection.AddSingleton<WatchHistory>();
        serviceCollection.AddHostedService<QueueRecovery>();
    }
}
