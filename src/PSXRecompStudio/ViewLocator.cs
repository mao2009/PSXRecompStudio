using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using PSXRecomp.Architecture;
using PSXRecompStudio.ViewModels;

namespace PSXRecompStudio;

[Application]
public class ViewLocator : IDataTemplate
{

    public Control? Build(object? param)
    {
        if (param is null)
            return null;
        
        var _name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var _type = Type.GetType(_name);

        if (_type != null)
        {
            return (Control)Activator.CreateInstance(_type)!;
        }

        return new TextBlock { Text = "Not Found: " + _name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
