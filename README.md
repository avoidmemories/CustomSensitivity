# Custom Sensitivity

A small Windows tool for setting an exact GTA V mouse sensitivity value in a `pc_settings.bin` profile.

It supports automatically detected GTA V Legacy and Enhanced profiles, as well as a manually selected profile file.

## Use

1. Close GTA V completely.
2. Select the intended game profile.
3. Enter an integer sensitivity value or use the slider.
4. Click **Apply sensitivity**.

The app creates a timestamped backup beside the selected `pc_settings.bin` before changing it.

The in-game slider normally covers `0` to `200`. Custom Sensitivity can write values outside that visual range when entered manually. Values below `-60` reverse camera input.

## Build

Requires the .NET 10 SDK on Windows.

```powershell
dotnet publish ".\Custom Sensitivity.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ".\publish"
```

The generated executable is `publish\Custom Sensitivity.exe`.

## License

MIT. See [LICENSE](LICENSE).
