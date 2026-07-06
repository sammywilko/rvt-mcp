using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ToggleToastCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.Instance == null)
                return Result.Failed;

            App.Instance.ToastEnabled = !App.Instance.ToastEnabled;
            if (!App.Instance.ToastEnabled)
                App.Instance.ToastNotifier?.DismissAll();

            return Result.Succeeded;
        }
    }
}
