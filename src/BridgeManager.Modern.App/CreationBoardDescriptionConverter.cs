using BridgeManager.Core.FirmwareModules;
using Microsoft.UI.Xaml.Data;

namespace BridgeManager.Modern;

// Presentation copy only. Board IDs, firmware matching and signed manifests stay unchanged.
public sealed class CreationBoardDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is BridgeBoardDefinition board ? board.Id switch
        {
            "bl616-devkit" => "BL616 开发板 · 通过下载串口安装固件",
            "esp32s3-n16r8" => "16 MB Flash / 8 MB PSRAM · 使用原生 USB 接口",
            "pico-2-w" => "带无线功能的 Pico 2 W · 通过 BOOTSEL 写入 UF2",
            "sf32lb52-devkit-nano" => "SF32LB52 Nano 开发板 · 通过串口安装固件",
            _ => board.Description
        } : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
