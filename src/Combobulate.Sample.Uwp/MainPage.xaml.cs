using System;
using System.IO;
using Combobulate.Parsing;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Combobulate.Sample.Uwp;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        this.InitializeComponent();
        LoadCube();
    }

    private void LoadCube()
    {
        combobulate.Model = ObjParser.Parse(CubeObj).Model;
        StatusText.Text = "Loaded: built-in cube";
    }

    private void ResetCube_Click(object sender, RoutedEventArgs e) => LoadCube();

    private void ResetRotation_Click(object sender, RoutedEventArgs e)
    {
        combobulate.RotationX = 0;
        combobulate.RotationY = 0;
        combobulate.RotationZ = 0;
    }

    private async void LoadObjButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".obj");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var text = await FileIO.ReadTextAsync(file);
            var result = ObjParser.Parse(text);

            if (result.Model.Quads.Count == 0)
            {
                StatusText.Text = $"{file.Name}: no quads found ({result.Errors.Count} errors)";
                return;
            }

            combobulate.Model = result.Model;
            StatusText.Text = result.Errors.Count == 0
                ? $"Loaded: {file.Name} ({result.Model.Quads.Count} quads)"
                : $"Loaded: {file.Name} ({result.Model.Quads.Count} quads, {result.Errors.Count} skipped)";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to load OBJ: {ex.Message}";
        }
    }

    // A unit cube centered on the origin, expressed as 6 quad faces.
    private const string CubeObj = @"
v -1 -1 -1
v  1 -1 -1
v  1  1 -1
v -1  1 -1
v -1 -1  1
v  1 -1  1
v  1  1  1
v -1  1  1

# +Z (front)
f 5 6 7 8
# -Z (back)
f 2 1 4 3
# +X (right)
f 6 2 3 7
# -X (left)
f 1 5 8 4
# +Y (top)
f 8 7 3 4
# -Y (bottom)
f 5 1 2 6
";
}
