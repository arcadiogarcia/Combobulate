global using System;

#if !COMBOBULATE_NO_XAML
#if !WINAPPSDK
global using Windows.UI.Xaml;
global using Windows.UI.Xaml.Controls;
#else
global using Microsoft.UI.Xaml;
global using Microsoft.UI.Xaml.Controls;
#endif
#endif
