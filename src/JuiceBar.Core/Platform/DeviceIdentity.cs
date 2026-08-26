using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace JuiceBar.Core.Platform;

/// <summary>
/// 이 장비를 다른 장비와 구분하는 식별자.
///
/// JuiceBar는 서버도 계정도 없이 장비마다 독립적으로 동작하므로, 데스크톱과
/// 노트북의 누적값·캘리브레이션이 섞이지 않게 저장 경로를 갈라 놓는 용도다.
/// exe만 복사해 옮겨도 새 장비에서는 자기 프로필이 새로 생긴다.
/// </summary>
public static class DeviceIdentity
{
    private static readonly Lazy<string> _id = new(Resolve);

    public static string Current => _id.Value;

    public static string FriendlyName => Environment.MachineName;

    private static string Resolve()
    {
        // Windows가 설치 때 부여하는 값이라 하드웨어 교체에도 안정적이다.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography", writable: false);

            if (key?.GetValue("MachineGuid") is string guid && !string.IsNullOrWhiteSpace(guid))
                return Shorten(guid);
        }
        catch (Exception)
        {
            // 정책으로 레지스트리 접근이 막힌 환경이 있다. 아래 대체 경로로 넘어간다.
        }

        return Shorten($"{Environment.MachineName}|{Environment.UserName}");
    }

    /// <summary>폴더 이름으로 쓸 수 있게 16자리로 줄인다.</summary>
    private static string Shorten(string source)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
