using System.Net.NetworkInformation;
using Windows.Networking.Connectivity;

namespace MeowLauncher.WinUI.Services;

internal static class NetworkAvailability
{
    internal const string OfflineMessage = "当前未检测到网络连接，请检查连接后重试。本地实例和离线账户仍可使用。";

    internal static bool CanStartRequest
    {
        get
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("MEOW_TEST_OFFLINE") == "1") return false;
#endif
            // A loopback proxy may operate without a system network profile.
            if (NetworkProxy.Proxy is System.Net.WebProxy { Address.IsLoopback: true }) return true;
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile is not null)
                    return profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None;
                // A local profile can still reach a service when the Windows connectivity probe is blocked.
                return NetworkInformation.GetConnectionProfiles()
                    .Any(p => p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None);
            }
            catch { return NetworkInterface.GetIsNetworkAvailable(); }
        }
    }
}
