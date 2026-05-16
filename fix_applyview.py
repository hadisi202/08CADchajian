"""Fix the ApplyView function in MyPlugin.cs"""
import re

filepath = r"D:\cursor\08CADchajian\FurniturePlugin\MyPlugin.cs"

with open(filepath, 'r', encoding='utf-8') as f:
    content = f.read()

# The old ApplyView function has a broken nested if structure.
# Replace it with the clean version.
old_applyview = '''\t\t\tvoid ApplyView(string choice)
\t\t\t{
\t\t\t\tViewTableRecord currentView = editor.GetCurrentView();
\t\t\t\ttry
\t\t\t\t{
\t\t\t\t\t((AbstractViewTableRecord)currentView).ViewTwist = 0.0;
\t\t\t\t\tif (choice == "侧视图")
\t\t\t\t\t{
\t\t\t\t\t\tif (choice == "侧视图")
\t\t\t\t\t\t{
\t\t\t\t\t\t\t((AbstractViewTableRecord)currentView).ViewDirection = new Vector3d(1.0, 0.0, 0.0);
\t\t\t\t\t\t}
\t\t\t\t\t\telse
\t\t\t\t\t\t{
\t\t\t\t\t\t\t((AbstractViewTableRecord)currentView).ViewDirection = new Vector3d(0.0, 0.0, 1.0);
\t\t\t\t\t\t}
\t\t\t\t\t}
\t\t\t\t\telse
\t\t\t\t\t{
\t\t\t\t\t\t((AbstractViewTableRecord)currentView).ViewDirection = new Vector3d(0.0, -1.0, 0.0);
\t\t\t\t\t}
\t\t\t\t\teditor.SetCurrentView(currentView);
\t\t\t\t}
\t\t\t\tfinally
\t\t\t\t{
\t\t\t\t\t((IDisposable)currentView)?.Dispose();
\t\t\t\t}
\t\t\t}'''

new_applyview = '''\t\t\tvoid ApplyView(string choice)
\t\t\t{
\t\t\t\tViewTableRecord currentView = editor.GetCurrentView();
\t\t\t\ttry
\t\t\t\t{
\t\t\t\t\t((AbstractViewTableRecord)currentView).ViewTwist = 0.0;
\t\t\t\t\tif (choice == "侧视图")
\t\t\t\t\t\t((AbstractViewTableRecord)currentView).ViewDirection = new Vector3d(1.0, 0.0, 0.0);
\t\t\t\t\telse if (choice == "正视图")
\t\t\t\t\t\t((AbstractViewTableRecord)currentView).ViewDirection = new Vector3d(0.0, -1.0, 0.0);
\t\t\t\t\telse
\t\t\t\t\t\t((AbstractViewTableRecord)currentView).ViewDirection = new Vector3d(0.0, 0.0, 1.0);
\t\t\t\t\teditor.SetCurrentView(currentView);
\t\t\t\t}
\t\t\t\tfinally
\t\t\t\t{
\t\t\t\t\t((IDisposable)currentView)?.Dispose();
\t\t\t\t}
\t\t\t}'''

if old_applyview in content:
    content = content.replace(old_applyview, new_applyview)
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(content)
    print("SUCCESS: ApplyView function fixed!")
else:
    # Try finding by signature
    idx = content.find('void ApplyView(string choice)')
    if idx >= 0:
        print(f"Found ApplyView at position {idx}")
        # Show surrounding context
        start = max(0, idx - 200)
        end = min(len(content), idx + 800)
        snippet = content[start:end]
        print("Context:")
        print(repr(snippet[:500]))
    else:
        print("Could not find ApplyView function!")
