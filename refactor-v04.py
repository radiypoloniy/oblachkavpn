from pathlib import Path

root = Path(__file__).parent / 'src'

def write(name, text):
    (root / name).write_text(text, encoding='utf-8')

# Move complete methods using the next method declaration as the boundary.
# The extracted files are explicit facets of the WPF view, not new services.
window = (root / 'MainWindow.xaml.cs').read_text(encoding='utf-8-sig')
header, body = window.split('public partial class MainWindow : Window', 1)
def move_window(names, filename):
    global body
    methods = []
    import re
    for name in names:
        start = re.search(r'^    (?:private|internal).*?\b' + name + r'\(', body, re.M).start()
        following = re.search(r'^    (?:private|internal) ', body[start+1:], re.M)
        end = start + 1 + following.start() if following else body.rfind('}')
        methods.append(body[start:end])
        body = body[:start] + body[end:]
    write(filename, header + 'public partial class MainWindow\n{\n' + ''.join(methods) + '}\n')

move_window(['LoadPreviewProfiles', 'CheckPreviewControls', 'RunUiFixtureAsync'], 'MainWindow.Fixtures.cs')
move_window(['Import', 'ApplyImport', 'ProfileSelectionChanged', 'ShowProfile', 'SubscriptionClick', 'RefreshClick', 'LoadSubscriptionAsync', 'ImportFileClick', 'PasteClick'], 'MainWindow.Import.cs')
move_window(['ScheduleRecovery', 'ExportReportClick'], 'MainWindow.Diagnostics.cs')
write('MainWindow.xaml.cs', header + 'public partial class MainWindow : Window' + body)

tests = (root / 'SelfTests.cs').read_text(encoding='utf-8-sig')
header = tests[:tests.index('public static class SelfTests')]
tests = tests.replace('public static class SelfTests', 'public static partial class SelfTests')
start = tests.index('        await CheckAsync("VLESS XHTTP')
split = tests.index('        await CheckAsync("Real Xray validates')
end = tests.index('        await File.WriteAllTextAsync(report')
groups = [('SelfTests.Import.cs', 'ImportChecksAsync', tests[start:split]),
          ('SelfTests.Engine.cs', 'EngineChecksAsync', tests[split:end])]
for filename, method, content in groups:
    write(filename, header + 'public static partial class SelfTests\n{\n' +
          f'    private static async Task {method}(Func<string, Func<Task>, Task> CheckAsync, string executable, string root)\n    {{\n' + content + '    }\n}\n')
tests = tests[:start] + '        await ImportChecksAsync(CheckAsync, executable, root);\n        await EngineChecksAsync(CheckAsync, executable, root);\n' + tests[end:]
start = tests.index('    private static async Task TunnelFixtureAsync')
end = tests.index('    private static bool IsAlive')
write('SelfTests.Tunnel.cs', header + 'public static partial class SelfTests\n{\n' + tests[start:end] + '}\n')
tests = tests[:start] + tests[end:]
tests = tests.replace('        Task Sync(Action action) { action(); return Task.CompletedTask; }', '')
line_start = tests.index('        const string link =')
line_end = tests.index('\n', line_start)
link = tests[line_start:line_end].replace('        const', '    private const')
tests = tests[:line_start] + tests[line_end:]
tests = tests.replace('    private sealed record Check', link + '\n    private static Task Sync(Action action) { action(); return Task.CompletedTask; }\n    private sealed record Check')
write('SelfTests.cs', tests)
