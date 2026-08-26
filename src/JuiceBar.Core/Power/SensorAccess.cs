namespace JuiceBar.Core.Power;

/// <summary>CPU 전력을 실측하기 위해 지금 무엇이 부족한지.</summary>
public enum SensorAdvice
{
    /// <summary>다 됐다. CPU 를 실측하고 있다.</summary>
    Measuring,

    /// <summary>드라이버가 없다. 설치하면 실측할 수 있다.</summary>
    InstallDriver,

    /// <summary>드라이버는 있는데 권한이 없다. 승격해서 다시 띄우면 된다.</summary>
    RunElevated,
}

/// <summary>
/// CPU 전력을 어떻게 읽을 수 있는지 판단한다.
///
/// 이 판단이 사용자에게 무엇을 권할지를 정한다. 틀리면 이미 잘 되고 있는 사람에게
/// 커널 드라이버를 깔라고 하거나, 깔아 둔 사람에게 또 깔라고 하게 된다.
///
/// 순서가 중요하다 — Windows 에너지 미터가 답이면 나머지는 물어볼 필요가 없다.
/// 미터는 Windows 11 에서 CPU 의 RAPL 값을 그대로 내보내므로 대개 여기서 끝난다.
/// Windows 10 은 실제 하드웨어 전력계가 달린 기기(Surface 계열 등)에만 있어서
/// 대부분 드라이버 경로로 넘어간다.
/// </summary>
public static class SensorAccess
{
    public static SensorAdvice Advise(bool hasEnergyMeter, bool driverInstalled, bool isElevated)
    {
        // 미터는 평범한 권한으로 열린다. 드라이버도 승격도 필요 없다.
        if (hasEnergyMeter) return SensorAdvice.Measuring;

        if (!driverInstalled) return SensorAdvice.InstallDriver;

        // 드라이버는 커널에 있지만, 말을 걸려면 승격된 프로세스여야 한다.
        return isElevated ? SensorAdvice.Measuring : SensorAdvice.RunElevated;
    }

    /// <summary>
    /// 이 상태를 사용자에게 한 번이라도 알려야 하는지.
    /// 이미 실측하고 있으면 방해할 이유가 없다.
    /// </summary>
    public static bool NeedsAttention(SensorAdvice advice)
        => advice != SensorAdvice.Measuring;
}
