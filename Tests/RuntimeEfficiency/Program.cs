using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using ANEVRED.Controls;
using ANEVRED.Models;
using ANEVRED.Services;
using ANEVRED.ViewModels;
class Program {
 static int checks;
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
 static void Field(object obj,string name,object? value)=>obj.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(obj,value);
 static void Property(object obj,string name,object value)=>Field(obj,$"<{name}>k__BackingField",value);
 static object? Invoke(object obj,string name,params object[] values)=>obj.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(obj,values);
 [STAThread] static void Main(string[] args){
  var app=new Application();
  var elapsed=TimeSpan.Zero;
  using(var diagnostics=new SelfDiagnosticsService(()=>elapsed)){
   Check(diagnostics.TrySample(),"Initial diagnostic sample");
   Check(!diagnostics.TrySample(),"Diagnostic throttle");
   var initial=diagnostics.Latest!.PrivateMemoryMb;
   for(int i=1;i<=250;i++){elapsed=TimeSpan.FromSeconds(i*5);Check(diagnostics.TrySample(),"Diagnostic timed sample");}
   Check(diagnostics.SampleCount==120,"Diagnostic buffer bounded");
   Check(Math.Abs(diagnostics.MemoryChangeMb-(diagnostics.Latest!.PrivateMemoryMb-initial))<0.001,"Lifetime baseline survives ring eviction");
   var path=Path.Combine(AppContext.BaseDirectory,"diagnostics-test.csv");
   var culture=CultureInfo.CurrentCulture;
   CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");
   diagnostics.Export(path);CultureInfo.CurrentCulture=culture;
   var lines=File.ReadAllLines(path);File.Delete(path);
   Check(lines.Length==121,"CSV only retained samples");
   Check(lines[0].StartsWith("AppVersion;TimeUtc;"),"Versioned CSV header");
   Check(lines.Skip(1).All(l=>l.Split(';').Length==8),"CSV column counts");
   Check(lines.Skip(1).All(l=>double.TryParse(l.Split(';')[2],NumberStyles.Float,CultureInfo.InvariantCulture,out _)),"Invariant numeric CSV");
  }
  var mutexName=@"Local\ANEVRED-test-"+Guid.NewGuid().ToString("N");
  using(var first=new SingleInstanceService(mutexName)){
   Check(first.Acquired,"First instance allowed");
   Check(Task.Run(()=>{using var second=new SingleInstanceService(mutexName);return !second.Acquired;}).Result,"Concurrent instance rejected");
  }
  using(var after=new SingleInstanceService(mutexName))Check(after.Acquired,"Instance lock released");
  var vm=(MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
  Property(vm,"L",new LocalizationService("de"));Property(vm,"Settings",AppSettings.CreateDefault("de"));
  Property(vm,"MetricCards",new ObservableCollection<MetricCard>());
  foreach(var name in new[]{"CpuHistory","RamHistory","GpuHistory","VramHistory","FrametimeHistory"}){
   var h=new ObservableCollection<double>(Enumerable.Range(0,3600).Select(i=>(double)(i%100)));Property(vm,name,h);
  }
  Property(vm,"Logs",new ObservableCollection<LogEntry>());
  var timer=new DispatcherTimer();Field(vm,"_timer",timer);Field(vm,"_currentView","Dashboard");Field(vm,"_isWindowVisible",true);
  var snapshot=new SystemSnapshot{CpuUsagePercent=20,RamUsagePercent=40,IsGpuDataAvailable=true};Field(vm,"_lastSnapshot",snapshot);
  Invoke(vm,"UpdateMetricCards",snapshot);var cards=vm.MetricCards.ToArray();
  Check(cards.Length==7,"Seven metric cards");Check(ReferenceEquals(cards[0].History,vm.CpuHistory),"History shared, not copied");
  int changes=0;cards[0].PropertyChanged+=(_,_)=>changes++;
  var oldValue=cards[0].Value;
  snapshot=new SystemSnapshot{CpuUsagePercent=80,RamUsagePercent=70,IsGpuDataAvailable=true};Field(vm,"_lastSnapshot",snapshot);
  for(int i=0;i<1000;i++)Invoke(vm,"UpdateMetricCards",snapshot);
  Check(vm.MetricCards.SequenceEqual(cards),"Card identity stable across 1000 updates");
  Check(changes>0&&cards[0].Value!=oldValue,"Existing card sends binding updates");
  Check(cards.All(c=>c.Accent.IsFrozen),"Card brushes frozen");
  vm.SetWindowVisible(false);Check(timer.Interval==TimeSpan.FromSeconds(5),"Hidden polling throttled");
  vm.SetWindowVisible(true);Check(timer.Interval==TimeSpan.FromSeconds(1),"Visible polling restored");
  var tempRoot=Path.Combine(AppContext.BaseDirectory,"settings-write-probe-"+Guid.NewGuid().ToString("N"));
  File.WriteAllText(tempRoot,"blocked directory");
  Field(vm,"_settingsService",new SettingsService(tempRoot));
  Invoke(vm,"SaveSettings");Check(vm.HasSettingsLoadWarning,"Save failure shown in UI");
  Check(vm.Logs.Count==1,"Save failure logged once");Invoke(vm,"SaveSettings");Check(vm.Logs.Count==1,"Repeated failure does not flood log");
  File.Delete(tempRoot);
  Invoke(vm,"SaveSettings");Check(!vm.HasSettingsLoadWarning,"Successful retry clears write warning");
  Directory.Delete(tempRoot,true);
  // Render the exact diagnostics panel with actual bindings and theme, without starting MainWindow.
  using var diag=new SelfDiagnosticsService();diag.TrySample();Field(vm,"_diagnosticsService",diag);
  Property(vm,"ExportDiagnosticsCommand",new RelayCommand(_=>{}));
  var project=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
  var document=XDocument.Load(Path.Combine(project,"MainWindow.xaml"));
  var x=XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
  var panelElement=document.Descendants().Single(e=>(string?)e.Attribute(x+"Name")=="SelfDiagnosticsPanel");
  app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(File.ReadAllText(Path.Combine(project,"Themes/DarkTheme.xaml"))));
  var panel=(Border)XamlReader.Parse(panelElement.ToString());panel.DataContext=vm;
  panel.Measure(new Size(940,500));panel.Arrange(new Rect(0,0,940,panel.DesiredSize.Height));panel.UpdateLayout();
  Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
  var bitmap=new RenderTargetBitmap(940,(int)Math.Ceiling(panel.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(panel);
  var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
  var imagePath=Path.Combine(project,"..","diagnostics-preview.png");using(var stream=File.Create(imagePath))png.Save(stream);
  Check(panel.ActualHeight>150,"Diagnostics panel rendered");
  Check(vm.DiagnosticsSummaryText.Contains("MB"),"Localized diagnostic values");
  Console.WriteLine($"PASS {checks} runtime/efficiency checks; preview {imagePath}");
 }
}
