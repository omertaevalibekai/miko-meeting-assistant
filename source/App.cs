using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace MeetingSprite {
    enum Activity { Idle, Starting, Recording, Stopping, Processing }
    public class Controller {
        public Window Widget,Preferences;
        public UserControl Panel;
        public PixelMascot Pet=new PixelMascot();
        public Popup Menu;
        readonly bool preview;
        Settings settings;
        Activity activity;
        Recorder recorder;
        Meeting active;
        CancellationTokenSource processing;
        bool quitting,loading,notesDirty,dragging,exitAfterProcessing,playing,historyLoaded;
        DateTime lastNoteSave=DateTime.MinValue;
        readonly Stopwatch clock=Stopwatch.StartNew();
        readonly List<Button> choices=new List<Button>();
        readonly MediaPlayer player=new MediaPlayer();
        readonly DispatcherTimer timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(200)};
        readonly DispatcherTimer animation=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(42)};
        EventWaitHandle wake;
        string status="Рядом с вашей встречей";

        public Controller(bool preview) {
            this.preview=preview;
            settings=preview?new Settings {Language="ru",Left=-1,Top=-1,Animate=true}:Store.LoadSettings();
            Widget=(Window)Load("Widget.xaml");Preferences=(Window)Load("Settings.xaml");Panel=(UserControl)Load("Panel.xaml");
            W<ContentControl>("RobotHost").Content=Pet;W<ContentControl>("BubbleHost").Content=Panel;
            Menu=W<Popup>("MenuPopup");Menu.PlacementTarget=Widget;Menu.Placement=PlacementMode.Custom;
            Menu.CustomPopupPlacementCallback=(size,target,offset)=>new[]{new CustomPopupPlacement(new Point(-size.Width-8,Math.Min(0,target.Height-size.Height)),PopupPrimaryAxis.Vertical),new CustomPopupPlacement(new Point(target.Width+8,Math.Min(0,target.Height-size.Height)),PopupPrimaryAxis.Vertical)};
            Menu.Closed+=(s,e)=>{SaveNotes();StopPlayer();P<Border>("ExitConfirm").Visibility=Visibility.Collapsed;};
            W<Button>("MenuButton").Click+=(s,e)=>{if(Menu.IsOpen)Menu.IsOpen=false;else OpenMenu();};
            W<Border>("DragZone").MouseLeftButtonDown+=(s,e)=>{
                Menu.IsOpen=false;dragging=true;
                try{Widget.DragMove();ClampPosition();SavePosition();}catch{}finally{dragging=false;}
            };
            Widget.MouseEnter+=(s,e)=>{Pet.Hover=true;};Widget.MouseLeave+=(s,e)=>{Pet.Hover=false;};
            Widget.Closing+=(s,e)=>{if(!quitting)e.Cancel=true;};
            Preferences.Closing+=(s,e)=>{if(!quitting){e.Cancel=true;S<PasswordBox>("ApiKey").Clear();Preferences.Hide();}};
            Panel.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){Menu.IsOpen=false;e.Handled=true;}};
            P<Button>("CloseBubble").Click+=(s,e)=>Menu.IsOpen=false;
            P<Button>("SettingsButton").Click+=(s,e)=>ShowSettings();
            P<Button>("ExitButton").Click+=(s,e)=>{if(activity==Activity.Idle)Quit();else P<Border>("ExitConfirm").Visibility=Visibility.Visible;};
            P<Button>("DismissExit").Click+=(s,e)=>P<Border>("ExitConfirm").Visibility=Visibility.Collapsed;
            P<Button>("ConfirmExit").Click+=async(s,e)=>await Exit();
            P<Button>("RecordButton").Click+=async(s,e)=>{if(activity==Activity.Idle)await Start();else if(activity==Activity.Recording)await Stop();};
            P<Button>("CancelProcessing").Click+=(s,e)=>{if(processing!=null)processing.Cancel();};
            P<RadioButton>("OnlineMode").IsChecked=settings.Online;
            P<RadioButton>("OnlineMode").Checked+=(s,e)=>ModeChanged();P<RadioButton>("RoomMode").Checked+=(s,e)=>ModeChanged();
            P<TextBox>("Notes").TextChanged+=(s,e)=>{if(!loading){notesDirty=true;P<TextBlock>("NoteSaved").Text="Сохраняю…";}};
            P<TabControl>("MainTabs").SelectionChanged+=(s,e)=>{
                if(e.Source==P<TabControl>("MainTabs")&&P<TabControl>("MainTabs").SelectedIndex==2&&!historyLoaded)RefreshHistory(null);
            };
            P<ComboBox>("History").SelectionChanged+=(s,e)=>{StopPlayer();ShowSelected();};
            P<ComboBox>("DocumentKind").SelectionChanged+=(s,e)=>ShowDocument();P<ComboBox>("DocumentKind").SelectedIndex=0;
            P<Button>("PlayAudio").Click+=(s,e)=>Play();
            P<Button>("CopyText").Click+=(s,e)=>{try{Clipboard.SetText(P<TextBox>("DocumentView").Text);SetStatus("Текст скопирован");}catch(Exception ex){Error(ex.Message);}};
            P<Button>("Retry").Click+=async(s,e)=>{var m=Selected();if(m!=null&&activity==Activity.Idle)await Process(m);};
            player.MediaEnded+=(s,e)=>StopPlayer();player.MediaFailed+=(s,e)=>{StopPlayer();Error("Не удалось воспроизвести запись: "+e.ErrorException.Message);};
            S<Button>("RefreshDevices").Click+=(s,e)=>RefreshDevices();
            S<Button>("SaveSettings").Click+=(s,e)=>{if(ApplySettings())Preferences.Hide();};
            S<ComboBox>("TranscriptionProvider").SelectionChanged+=(s,e)=>ProviderChanged();
            S<Button>("OpenFiles").Click+=(s,e)=>{try{Store.Open(Store.Meetings);}catch(Exception ex){S<TextBlock>("SettingsHint").Text=ex.Message;}};
            S<TextBox>("DataPath").Text=Store.Meetings;
            foreach(var pair in new[]{new[]{"Русский","ru"},new[]{"English","en"},new[]{"Қазақша","kk"},new[]{"Авто","auto"}})S<ComboBox>("Language").Items.Add(new ComboBoxItem{Content=pair[0],Tag=pair[1]});
            LoadSettingsControls();RefreshDevices();BuildChoices();SelectMascot(settings.Mascot,false);ModeChanged();
            string draft=Path.Combine(Store.Root,"draft-notes.txt");
            if(!preview&&File.Exists(draft)){loading=true;P<TextBox>("Notes").Text=File.ReadAllText(draft,Store.Utf8);loading=false;}
            ShowSelected();UpdateState();
            Rect area=SystemParameters.WorkArea;
            bool placed=settings.Left!=-1||settings.Top!=-1;
            Widget.Left=placed&&Finite(settings.Left)?settings.Left:area.Right-184;Widget.Top=placed&&Finite(settings.Top)?settings.Top:area.Bottom-202;
            Widget.SourceInitialized+=(s,e)=>ClampPosition();
            timer.Tick+=async(s,e)=>await Tick();
            animation.Tick+=(s,e)=>{if(!dragging){Pet.Phase=clock.Elapsed.TotalSeconds;Pet.Animate=(settings.Animate??true)&&SystemParameters.ClientAreaAnimation;if(Pet.Animate)Pet.InvalidateVisual();}};
            if(!preview){wake=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Miko-ShowMenu-v2");timer.Start();animation.Start();}
        }
        static object Load(string name){using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream(name))return XamlReader.Load(s);}
        T P<T>(string name)where T:class{return Panel.FindName(name)as T;}
        T W<T>(string name)where T:class{return Widget.FindName(name)as T;}
        T S<T>(string name)where T:class{return Preferences.FindName(name)as T;}
        public void OpenMenu(){ClampPosition();Panel.Height=Math.Min(585,WorkArea().Height-24);P<Border>("ExitConfirm").Visibility=Visibility.Collapsed;Widget.Activate();Menu.IsOpen=true;}
        Rect WorkArea(){
            var handle=new WindowInteropHelper(Widget).Handle;if(handle==IntPtr.Zero)return SystemParameters.WorkArea;
            var bounds=System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var source=PresentationSource.FromVisual(Widget);var matrix=source==null?Matrix.Identity:source.CompositionTarget.TransformFromDevice;
            var p=matrix.Transform(new Point(bounds.Left,bounds.Top));var end=matrix.Transform(new Point(bounds.Right,bounds.Bottom));return new Rect(p,end);
        }
        void ClampPosition(){var a=WorkArea();Widget.Left=Math.Max(a.Left,Math.Min(Widget.Left,a.Right-Widget.Width));Widget.Top=Math.Max(a.Top,Math.Min(Widget.Top,a.Bottom-Widget.Height));}
        void SavePosition(){settings.Left=Widget.Left;settings.Top=Widget.Top;PersistSettings();}
        void PersistSettings(){if(preview)return;try{Store.SaveSettings(settings);}catch(Exception ex){Error("Настройки не сохранены: "+ex.Message);}}
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        void SetStatus(string value){if(!Widget.Dispatcher.CheckAccess()){Widget.Dispatcher.Invoke(new Action(()=>SetStatus(value)));return;}status=value;P<TextBlock>("StatusText").Text=value;}
        void Error(string text){P<TextBlock>("ErrorText").Text=text;P<TextBlock>("ErrorText").Visibility=String.IsNullOrEmpty(text)?Visibility.Collapsed:Visibility.Visible;if(!String.IsNullOrEmpty(text))W<TextBlock>("WidgetStatus").Text="Сообщение · ⋯";}
        void ModeChanged(){
            settings.Online=P<RadioButton>("OnlineMode").IsChecked==true;
            P<TextBlock>("ModeHint").Text=(settings.Online?"Микрофон + весь звук выбранных наушников или колонок.":"Записывается только микрофон.")+(settings.TranscriptionProvider=="local"?" Распознавание на компьютере.":" После остановки аудио отправляется в OpenAI · whisper-1. Нужны интернет и API-ключ.");
        }
        void LoadSettingsControls(){
            S<ComboBox>("Language").SelectedItem=S<ComboBox>("Language").Items.Cast<ComboBoxItem>().FirstOrDefault(x=>(string)x.Tag==settings.Language)??S<ComboBox>("Language").Items[0];S<CheckBox>("Animate").IsChecked=settings.Animate??true;
            S<ComboBox>("TranscriptionProvider").SelectedIndex=settings.TranscriptionProvider=="local"?1:0;
            S<PasswordBox>("ApiKey").Clear();S<CheckBox>("DeleteApiKey").IsChecked=false;
            S<TextBlock>("ApiKeyStatus").Text=!preview&&ApiKeyStore.Exists?"Ключ сохранён. Оставьте поле пустым, чтобы сохранить текущий.":"Ключ ещё не сохранён. Введите его здесь и нажмите «Готово».";ProviderChanged();
        }
        void ProviderChanged(){
            bool api=S<ComboBox>("TranscriptionProvider").SelectedIndex!=1;
            S<StackPanel>("ApiKeyPanel").Visibility=api?Visibility.Visible:Visibility.Collapsed;
            S<TextBlock>("ProviderHint").Text=api?"Запись работает офлайн. Для расшифровки аудио передаётся в OpenAI; API оплачивается отдельно. Ключ защищён вашей учётной записью Windows.":"Аудио распознаётся локально моделью Whisper base. Интернет и ключ не нужны.";
        }
        void RefreshDevices(){
            try{
                var mic=DeviceChoice.Get(DataFlow.Capture);var output=DeviceChoice.Get(DataFlow.Render);
                S<ComboBox>("Microphone").ItemsSource=mic;S<ComboBox>("Microphone").SelectedItem=mic.FirstOrDefault(x=>x.Id==settings.Mic)??mic.FirstOrDefault();
                S<ComboBox>("OutputDevice").ItemsSource=output;S<ComboBox>("OutputDevice").SelectedItem=output.FirstOrDefault(x=>x.Id==settings.Output)??output.FirstOrDefault();
                if(String.IsNullOrEmpty(settings.Mic)&&mic.Count>0)settings.Mic=mic[0].Id;
                if(String.IsNullOrEmpty(settings.Output)&&output.Count>0)settings.Output=output[0].Id;
            }catch(Exception ex){Error("Не удалось прочитать устройства: "+ex.Message);}
        }
        void ShowSettings(){Menu.IsOpen=false;LoadSettingsControls();RefreshDevices();UpdateState();Preferences.Height=Math.Min(760,SystemParameters.WorkArea.Height-32);Preferences.Show();Preferences.WindowState=WindowState.Normal;Preferences.Activate();}
        bool ApplySettings(){
            try {
            if(activity==Activity.Idle){
                if(!preview){if(!String.IsNullOrWhiteSpace(S<PasswordBox>("ApiKey").Password))ApiKeyStore.Save(S<PasswordBox>("ApiKey").Password);else if(S<CheckBox>("DeleteApiKey").IsChecked==true)ApiKeyStore.Delete();}
                settings.TranscriptionProvider=S<ComboBox>("TranscriptionProvider").SelectedIndex==1?"local":"openai";
                settings.Mic=(S<ComboBox>("Microphone").SelectedItem as DeviceChoice??new DeviceChoice()).Id;settings.Output=(S<ComboBox>("OutputDevice").SelectedItem as DeviceChoice??new DeviceChoice()).Id;settings.Language=(string)((ComboBoxItem)S<ComboBox>("Language").SelectedItem).Tag;
            }
            settings.Animate=S<CheckBox>("Animate").IsChecked==true;Pet.Animate=(settings.Animate??true)&&SystemParameters.ClientAreaAnimation;Pet.InvalidateVisual();PersistSettings();
            S<PasswordBox>("ApiKey").Clear();ModeChanged();return true;
            }catch(Exception ex){S<TextBlock>("SettingsHint").Text=ex.Message;return false;}
        }
        void BuildChoices(){
            for(int i=0;i<5;i++){
                int kind=i;var button=new Button{Margin=new Thickness(0,0,0,7),Padding=new Thickness(10,4,12,4),HorizontalContentAlignment=HorizontalAlignment.Stretch};
                var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(52)});grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(24)});
                var thumb=new Viewbox{Width=43,Height=54,Child=new PixelMascot{Kind=i,Animate=false}};grid.Children.Add(thumb);
                var label=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,0,0)};label.Children.Add(new TextBlock{Text=PixelMascot.Names[i],FontSize=14,FontWeight=FontWeights.SemiBold});label.Children.Add(new TextBlock{Text=PixelMascot.Descriptions[i],FontSize=11,Foreground=PixelMascot.Ink("#797384"),Margin=new Thickness(0,3,0,0)});Grid.SetColumn(label,1);grid.Children.Add(label);
                var tick=new TextBlock{Text="✓",VerticalAlignment=VerticalAlignment.Center,FontSize=16};Grid.SetColumn(tick,2);grid.Children.Add(tick);
                button.Content=grid;button.Tag=tick;button.Click+=(s,e)=>SelectMascot(kind,true);choices.Add(button);P<StackPanel>("MascotChoices").Children.Add(button);
            }
        }
        void SelectMascot(int kind,bool save){
            settings.Mascot=Math.Max(0,Math.Min(4,kind));Pet.Kind=settings.Mascot;Pet.InvalidateVisual();
            P<TextBlock>("PetName").Text=PixelMascot.Names[Pet.Kind];
            for(int i=0;i<choices.Count;i++){choices[i].Background=PixelMascot.Ink(i==Pet.Kind?"#E4DCF3":"#FFFFFF");((TextBlock)choices[i].Tag).Visibility=i==Pet.Kind?Visibility.Visible:Visibility.Hidden;}
            if(activity==Activity.Idle)W<TextBlock>("WidgetStatus").Text=PixelMascot.Names[Pet.Kind]+" · ⋯";
            if(save)PersistSettings();
        }
        void SaveNotes(){
            if(preview||!notesDirty)return;
            try{Store.Write(active==null?Path.Combine(Store.Root,"draft-notes.txt"):Path.Combine(active.Folder,"notes.txt"),P<TextBox>("Notes").Text);notesDirty=false;P<TextBlock>("NoteSaved").Text="Сохранено";}
            catch(Exception ex){Error("Заметки не сохранены: "+ex.Message);P<TextBlock>("NoteSaved").Text="Не удалось сохранить";}finally{lastNoteSave=DateTime.Now;}
        }
        void UpdateState(){
            bool idle=activity==Activity.Idle,recording=activity==Activity.Recording;
            P<Button>("RecordButton").IsEnabled=idle||recording;
            P<Button>("RecordButton").Content=recording?"■  Остановить и сохранить":idle?"●  Начать запись":activity==Activity.Processing?"Разбираю встречу…":"Сохраняю…";
            P<Button>("RecordButton").Background=PixelMascot.Ink(recording?"#C55B62":"#7463D5");
            P<Button>("CancelProcessing").Visibility=activity==Activity.Processing?Visibility.Visible:Visibility.Collapsed;
            foreach(string n in new[]{"MeetingTitle","RoomMode","OnlineMode"})P<Control>(n).IsEnabled=idle;
            foreach(string n in new[]{"Microphone","OutputDevice","Language","RefreshDevices","TranscriptionProvider","ApiKey","DeleteApiKey"})S<Control>(n).IsEnabled=idle;
            S<TextBlock>("SettingsHint").Text=idle?"Изменения сохраняются по кнопке «Готово».":"Устройства можно сменить после завершения встречи.";
            P<Button>("Retry").IsEnabled=idle&&Selected()!=null;
            P<Button>("PlayAudio").IsEnabled=idle&&Selected()!=null&&File.Exists(Path.Combine(Selected().Folder,"audio.wav"));
            Pet.State=recording?"recording":activity==Activity.Processing?"processing":"idle";Pet.InvalidateVisual();
            P<TextBlock>("NoteHint").Text=recording?"Заметки прикрепляются к текущей встрече.":"Черновик для следующей встречи. Сохраняется автоматически.";
            if(idle){W<TextBlock>("WidgetStatus").Text=P<TextBlock>("ErrorText").Visibility==Visibility.Visible?"Сообщение · ⋯":status=="Встреча готова"?"Встреча готова · ⋯":PixelMascot.Names[Pet.Kind]+" · ⋯";P<ProgressBar>("MicMeter").Value=0;P<ProgressBar>("OutputMeter").Value=0;Pet.Level=0;}
        }
        async Task Tick(){
            if(wake!=null&&wake.WaitOne(0))OpenMenu();
            if(recorder!=null&&activity==Activity.Recording){
                P<TextBlock>("TimerText").Text=TimeSpan.FromSeconds(recorder.Seconds).ToString(@"hh\:mm\:ss");W<TextBlock>("WidgetStatus").Text="● "+P<TextBlock>("TimerText").Text;
                P<ProgressBar>("MicMeter").Value=recorder.MicPeak;P<ProgressBar>("OutputMeter").Value=recorder.OutputPeak;Pet.Level=Math.Max(recorder.MicPeak,recorder.OutputPeak);if(!Pet.Animate)Pet.InvalidateVisual();
                if(!String.IsNullOrEmpty(recorder.Fault)){string fault=recorder.Fault;await Stop();Error("Запись остановлена: "+fault);}
                else if(recorder.Seconds>=7200){await Stop();Error("Достигнут лимит 2 часа. Можно начать следующую запись.");}
            }
            if(notesDirty&&(DateTime.Now-lastNoteSave).TotalSeconds>=1)SaveNotes();
            if(playing){P<TextBlock>("PlaybackTime").Text=player.Position.ToString(@"mm\:ss")+(player.NaturalDuration.HasTimeSpan?" / "+player.NaturalDuration.TimeSpan.ToString(@"mm\:ss"):"");}
        }
        async Task Start(){
            if(activity!=Activity.Idle)return;Error(null);ModeChanged();PersistSettings();StopPlayer();
            if(String.IsNullOrEmpty(settings.Mic)){Error("Выберите микрофон через «Настройки».");return;}
            if(settings.Online&&String.IsNullOrEmpty(settings.Output)){Error("Выберите выход звука через «Настройки».");return;}
            activity=Activity.Starting;UpdateState();SetStatus("Подключаю запись…");
            try{
                var mic=(S<ComboBox>("Microphone").ItemsSource as List<DeviceChoice>??new List<DeviceChoice>()).FirstOrDefault(x=>x.Id==settings.Mic);
                var output=(S<ComboBox>("OutputDevice").ItemsSource as List<DeviceChoice>??new List<DeviceChoice>()).FirstOrDefault(x=>x.Id==settings.Output);
                active=Store.Create(P<TextBox>("MeetingTitle").Text,settings.Online,settings.Language,mic==null?settings.Mic:mic.Name,settings.Online?(output==null?settings.Output:output.Name):null);
                Store.Write(Path.Combine(active.Folder,"notes.txt"),P<TextBox>("Notes").Text);Store.Write(Path.Combine(Store.Root,"draft-notes.txt"),"");notesDirty=false;
                var r=new Recorder();await Task.Run(()=>r.Start(active,settings.Mic,settings.Output));recorder=r;activity=Activity.Recording;SetStatus("Записываю встречу");historyLoaded=false;
            }catch(Exception ex){if(active!=null){active.Status="failed";active.Error=ex.Message;Store.Save(active);}active=null;activity=Activity.Idle;Error("Не удалось начать запись. Проверьте устройства и доступ Windows к микрофону. "+ex.Message);SetStatus("Запись не началась");}
            finally{UpdateState();}
        }
        async Task Stop(){
            if(activity!=Activity.Recording||recorder==null)return;
            activity=Activity.Stopping;UpdateState();SetStatus("Сохраняю аудио…");SaveNotes();var r=recorder;recorder=null;var m=active;
            try{
                m.Seconds=await Task.Run(()=>r.Stop());m.Status="saved";m.Warning=r.Fault;Store.Save(m);active=null;
                loading=true;P<TextBox>("Notes").Text="";loading=false;notesDirty=false;
                await Process(m);
            }catch(Exception ex){Error("Запись не завершена: "+ex.Message);SetStatus("Проверьте сохранённые файлы");}
            finally{active=null;activity=Activity.Idle;UpdateState();}
        }
        async Task Process(Meeting m){
            StopPlayer();activity=Activity.Processing;processing=new CancellationTokenSource();UpdateState();W<TextBlock>("WidgetStatus").Text="Разбираю встречу";
            bool ready=false;
            try{Error(null);await Processor.Run(m,SetStatus,processing.Token,settings.TranscriptionProvider);ready=true;RefreshHistory(m.Folder);P<TabControl>("MainTabs").SelectedIndex=2;}
            catch(OperationCanceledException){SetStatus("Аудио сохранено. Можно обработать позже.");}
            catch(Exception ex){Error(ex.Message);SetStatus("Аудио сохранено. Обработка не завершена.");}
            finally{processing.Dispose();processing=null;activity=Activity.Idle;RefreshHistory(m.Folder);UpdateState();if(ready)W<TextBlock>("WidgetStatus").Text="Встреча готова · ⋯";if(exitAfterProcessing)Quit();}
        }
        Meeting Selected(){return P<ComboBox>("History").SelectedItem as Meeting;}
        void RefreshHistory(string folder){
            if(folder==null&&Selected()!=null)folder=Selected().Folder;
            var list=preview?new List<Meeting>():Store.List().Where(m=>active==null||m.Folder!=active.Folder).ToList();
            P<ComboBox>("History").ItemsSource=list;historyLoaded=true;
            if(folder!=null)P<ComboBox>("History").SelectedItem=list.FirstOrDefault(x=>x.Folder==folder);
            ShowSelected();
        }
        void ShowSelected(){
            var m=Selected();P<ComboBox>("DocumentKind").IsEnabled=m!=null;
            P<TextBlock>("SelectedInfo").Text=m==null?"Выберите встречу. Новые записи появятся здесь.":m.Started+" · "+TimeSpan.FromSeconds(m.Seconds).ToString(@"hh\:mm\:ss")+(String.IsNullOrEmpty(m.TranscriptionModel)?"":"\n"+m.TranscriptionModel)+(String.IsNullOrEmpty(m.Warning)?"":"\n"+m.Warning)+(String.IsNullOrEmpty(m.Error)?"":"\n"+m.Error);
            ShowDocument();UpdateState();
        }
        void ShowDocument(){
            var m=Selected();string text="";
            if(m!=null){var selected=P<ComboBox>("DocumentKind").SelectedItem as ComboBoxItem;if(selected!=null){string file=Path.Combine(m.Folder,(string)selected.Tag);try{text=File.Exists(file)?File.ReadAllText(file,Store.Utf8):"Результат ещё не создан. Нажмите «Повторить».";}catch(Exception ex){text=ex.Message;}}}
            P<TextBox>("DocumentView").Text=text;P<Button>("CopyText").IsEnabled=!String.IsNullOrEmpty(text);
        }
        void Play(){
            if(playing){StopPlayer();return;}var m=Selected();if(m==null||activity!=Activity.Idle)return;
            try{player.Open(new Uri(Path.Combine(m.Folder,"audio.wav")));player.Volume=0.8;player.Play();playing=true;P<Button>("PlayAudio").Content="■ Стоп";}
            catch(Exception ex){Error(ex.Message);}
        }
        void StopPlayer(){player.Stop();player.Close();playing=false;P<Button>("PlayAudio").Content="▶ Слушать";P<TextBlock>("PlaybackTime").Text="";}
        async Task Exit(){
            if(activity==Activity.Starting||activity==Activity.Stopping){Error("Дождитесь завершения операции с аудио.");return;}
            if(activity==Activity.Processing){exitAfterProcessing=true;processing.Cancel();return;}
            if(recorder!=null){activity=Activity.Stopping;UpdateState();SaveNotes();var r=recorder;recorder=null;active.Seconds=await Task.Run(()=>r.Stop());active.Status="saved";active.Warning=r.Fault;Store.Save(active);active=null;notesDirty=false;}
            Quit();
        }
        void Quit(){SaveNotes();SavePosition();quitting=true;animation.Stop();timer.Stop();StopPlayer();Menu.IsOpen=false;if(wake!=null)wake.Dispose();Application.Current.Shutdown();}
        public void ReportError(Exception ex){try{Store.Write(Path.Combine(Store.Root,"runtime-error.txt"),ex.ToString());}catch{}Error("Не удалось завершить действие: "+ex.Message);}

        public void UiTest(string folder){
            Directory.CreateDirectory(folder);
            if(Preferences.IsVisible)throw new Exception("Settings opened at startup");
            if(((Grid)Widget.Content).Children.OfType<Button>().Count()!=1||Widget.ContextMenu!=null)throw new Exception("Widget must expose only ellipsis button");
            Widget.Show();W<Button>("MenuButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            if(!Menu.IsOpen||Menu.PlacementTarget!=Widget||Preferences.IsVisible||!P<TextBox>("MeetingTitle").IsVisible)throw new Exception("Dots must open anchored popup with recording controls only");
            for(int i=0;i<5;i++){choices[i].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));if(Pet.Kind!=i||settings.Mascot!=i)throw new Exception("Mascot selection failed");}
            string[] contents={"MeetingTitle","Notes","History","MascotChoices"};
            for(int tab=0;tab<4;tab++){P<TabControl>("MainTabs").SelectedIndex=tab;Pump();if(Preferences.IsVisible||!P<FrameworkElement>(contents[tab]).IsVisible)throw new Exception("Popup tab failed to show its content");}
            P<Button>("SettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();if(!Preferences.IsVisible||Menu.IsOpen)throw new Exception("Settings route failed");
            if(S<ComboBox>("TranscriptionProvider").SelectedIndex!=0||!S<PasswordBox>("ApiKey").IsVisible)throw new Exception("OpenAI must be the default with a masked key input");
            S<ComboBox>("TranscriptionProvider").SelectedIndex=1;if(S<StackPanel>("ApiKeyPanel").Visibility!=Visibility.Collapsed)throw new Exception("Local mode must hide API controls");
            ApplySettings();if(settings.TranscriptionProvider!="local")throw new Exception("Local provider was not saved");
            S<ComboBox>("TranscriptionProvider").SelectedIndex=0;ApplySettings();if(settings.TranscriptionProvider!="openai"||!P<TextBlock>("ModeHint").Text.Contains("OpenAI"))throw new Exception("API provider was not saved");
            Preferences.Hide();P<RadioButton>("OnlineMode").IsChecked=true;if(!settings.Online)throw new Exception("Online mode failed");P<RadioButton>("RoomMode").IsChecked=true;
            P<TabControl>("MainTabs").SelectedIndex=0;SelectMascot(0,false);Widget.Hide();
            var a=new PixelMascot{Kind=0,Phase=0.7,Animate=true};var b=new PixelMascot{Kind=0,Phase=4.65,Animate=true};
            byte[] first=Pixels(a),second=Pixels(b);if(first.SequenceEqual(second))throw new Exception("Animation frames did not change");
            a.Animate=false;b.Animate=false;if(!Pixels(a).SequenceEqual(Pixels(b)))throw new Exception("Reduced motion must freeze animation");
            var shapes=Enumerable.Range(0,5).Select(i=>Convert.ToBase64String(Pixels(new PixelMascot{Kind=i,Animate=false}))).ToList();
            if(shapes.Distinct().Count()!=5)throw new Exception("Mascots must have different renders");
            var json=new System.Web.Script.Serialization.JavaScriptSerializer();
            var legacy=json.Deserialize<Settings>("{\"Language\":\"ru\",\"Online\":false,\"Left\":20,\"Top\":30}");
            if(legacy.Mascot!=0||!(legacy.Animate??true))throw new Exception("Legacy settings migration failed");
            var restored=json.Deserialize<Settings>(json.Serialize(new Settings{Mascot=4,Animate=false}));if(restored.Mascot!=4||restored.Animate!=false)throw new Exception("Appearance settings round trip failed");
            Store.Write(Path.Combine(folder,"ui-results.txt"),"PASS: only one widget action (ellipsis); anchored popup opens; five distinct selectable mascots; four workflow tabs stay in popup; only settings opens a normal window; mode switching; animated frames differ; disabled animation stays still. No microphone capture started.");
            Render(folder);
        }
        static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
        static void Settle(){var frame=new DispatcherFrame();var wait=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};wait.Tick+=(s,e)=>{wait.Stop();frame.Continue=false;};wait.Start();Dispatcher.PushFrame(frame);}
        static byte[] Pixels(PixelMascot pet){var bitmap=Preview.Render(pet,114,144,null);byte[] data=new byte[114*144*4];bitmap.CopyPixels(data,114*4,0);return data;}
        public void Render(string folder){
            Directory.CreateDirectory(folder);
            Widget.Show();SelectMascot(0,false);P<TabControl>("MainTabs").SelectedIndex=0;OpenMenu();Settle();
            Preview.Save(Panel,430,585,Path.Combine(folder,"menu.png"),null);
            Preview.Save(Widget.Content as FrameworkElement,160,178,Path.Combine(folder,"mascot.png"),null);
            P<TabControl>("MainTabs").SelectedIndex=3;Settle();Preview.Save(Panel,430,585,Path.Combine(folder,"choose-mascot.png"),null);
            Menu.IsOpen=false;Preferences.Show();Settle();Preview.Save(Preferences.Content as FrameworkElement,534,720,Path.Combine(folder,"settings.png"),Preferences.Background);Preferences.Hide();Widget.Hide();
            Preview.Mascots(folder);
        }
    }
    public static class Preview {
        public static RenderTargetBitmap Render(FrameworkElement element,int width,int height,Brush background){
            element.InvalidateVisual();element.Measure(new Size(width,height));element.Arrange(new Rect(0,0,width,height));element.UpdateLayout();
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
            if(background!=null){var visual=new DrawingVisual();using(var d=visual.RenderOpen())d.DrawRectangle(background,null,new Rect(0,0,width,height));bitmap.Render(visual);}bitmap.Render(element);return bitmap;
        }
        public static void Save(FrameworkElement e,int w,int h,string path,Brush bg){var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(Render(e,w,h,bg)));using(var file=File.Create(path))enc.Save(file);}
        public static void Mascots(string folder){
            Save(Sheet(0,false),1000,300,Path.Combine(folder,"five-mascots.png"),null);
        }
        public static void Animation(string folder){Directory.CreateDirectory(folder);for(int i=0;i<60;i++)Save(Sheet(i/12.0,true),1000,300,Path.Combine(folder,"frame-"+i.ToString("000")+".png"),null);}
        static FrameworkElement Sheet(double phase,bool animate){
            var surface=new StackPanel{Background=PixelMascot.Ink("#F4F4FA"),Width=1000,Height=300};
            surface.Children.Add(new TextBlock{Text="Пять характеров Мико",FontFamily=new FontFamily("Segoe UI"),FontSize=24,FontWeight=FontWeights.SemiBold,Foreground=PixelMascot.Ink("#242532"),Margin=new Thickness(25,18,0,16)});
            var row=new System.Windows.Controls.Primitives.UniformGrid{Columns=5};surface.Children.Add(row);
            for(int i=0;i<5;i++){var col=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center};col.Children.Add(new PixelMascot{Kind=i,Phase=phase,Animate=animate,HorizontalAlignment=HorizontalAlignment.Center});col.Children.Add(new TextBlock{Text=PixelMascot.Names[i],HorizontalAlignment=HorizontalAlignment.Center,FontFamily=new FontFamily("Segoe UI"),FontSize=18,FontWeight=FontWeights.SemiBold,Foreground=PixelMascot.Ink("#242532"),Margin=new Thickness(0,7,0,5)});col.Children.Add(new TextBlock{Text=PixelMascot.Descriptions[i],HorizontalAlignment=HorizontalAlignment.Center,FontFamily=new FontFamily("Segoe UI"),FontSize=12,Foreground=PixelMascot.Ink("#797384")});row.Children.Add(col);}
            return surface;
        }
    }
    public static class Program {
        [STAThread]public static int Main(string[] args){
            try{
                if(args.Length>0&&args[0]=="--self-test")return Tests.Run(args[1]);
                if(args.Length>0&&args[0]=="--api-tests")return ApiTests.Run(args[1]);
                if(args.Length>0&&args[0]=="--devices"){Store.Write(args[1],new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{Microphones=DeviceChoice.Get(DataFlow.Capture),Outputs=DeviceChoice.Get(DataFlow.Render)}));return 0;}
                if(args.Length>0&&args[0]=="--transcribe-test")return Tests.Transcribe(args[1],args[2]);
                if(args.Length>0&&args[0]=="--processing-tests")return Tests.ProcessingEdges(args[1]);
                var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
                if(args.Length>0&&args[0]=="--animation-preview"){Preview.Animation(args[1]);return 0;}
                if(args.Length>0&&args[0]=="--render"){new Controller(true).Render(args[1]);return 0;}
                if(args.Length>0&&args[0]=="--ui-test"){new Controller(true).UiTest(args[1]);return 0;}
                bool created;using(var mutex=new Mutex(true,"Local\\MeetingSprite-Miko-v1",out created)){
                    if(!created){try{using(var signal=EventWaitHandle.OpenExisting("Local\\Miko-ShowMenu-v2"))signal.Set();}catch{}return 0;}
                    var controller=new Controller(false);app.MainWindow=controller.Widget;
                    app.DispatcherUnhandledException+=(s,e)=>{controller.ReportError(e.Exception);e.Handled=true;};
                    controller.Widget.Show();app.Run();
                }
                return 0;
            }catch(Exception ex){try{Store.Write(Path.Combine(Store.Root,"startup-error.txt"),ex.ToString());}catch{}return 1;}
        }
    }
}
