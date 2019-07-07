using System.ComponentModel.Composition;
using dnlib.DotNet;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Menus;
using HoLLy.dnSpyExtension.SourceMap;

namespace HoLLy.dnSpyExtension.Commands.SourceMap
{
    [ExportMenuItem(Header = "Change displayed name", Group = Constants.ContextMenuGroupEdit)]
    internal class RenameDisplayedCommand : MenuItemBase
    {
        private readonly ISourceMapStorage sourceMapStorage;

        [ImportingConstructor]
        public RenameDisplayedCommand(ISourceMapStorage sourceMapStorage)
        {
            this.sourceMapStorage = sourceMapStorage;
        }

        public override void Execute(IMenuItemContext context)
        {
            var textReference = context.Find<TextReference>();
            var docViewer = context.Find<IDocumentViewer>();
            var m = (IMemberDef)textReference.Reference;

            string newName = MsgBox.Instance.Ask<string>(string.Empty, title: $"New name for {m.Name}");
            if (!string.IsNullOrEmpty(newName))
                sourceMapStorage.SetName(m, newName);

            var documentTabService = docViewer.DocumentTab.DocumentTabService;
            documentTabService.RefreshModifiedDocument(documentTabService.DocumentTreeView.FindNode(m.Module).Document);
        }

        public override bool IsVisible(IMenuItemContext context)
        {
            var tf = context.Find<TextReference>();
            return tf?.Reference is IMemberDef;
        }
    }
}
