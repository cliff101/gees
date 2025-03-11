using CTrue.FsConnect;
using Microsoft.FlightSimulator.SimConnect;
using Octokit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TaskbarClock;
using MouseEventArgs = System.Windows.Forms.MouseEventArgs;



namespace GeesWPF
{
    public enum Requests
    {
        PlaneInfo = 0
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct PlaneInfoResponse
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Type;
        public bool OnGround;
        public double WindLat;
        public double WindHead;
        public double AirspeedInd;
        public double GroundSpeed;
        public double LateralSpeed;
        public double ForwardSpeed;
        public double Gforce;
        public double LandingRate;
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        #region Publics and statics
        static bool ShowLanding = false;
        static bool LandingComplete = false;
        static bool SafeToRead = true;
        static List<PlaneInfoResponse> Landingdata = new List<PlaneInfoResponse>();
        static FsConnect fsConnect = new FsConnect();
        static List<SimVar> definition = new List<SimVar>();
        static string updateUri;
        static public string version;
        int lastDeactivateTick;
        bool lastDeactivateValid;
        int bounces = 0;
        int myDefineId;

        const int SAMPLE_RATE = 1000/60; //ms
        static int BUFFER_SIZE = 200;
        static int BUFFER_SIZE_SHOW = 10;

        DispatcherTimer timerRead = new DispatcherTimer();
        DispatcherTimer timerBounce = new DispatcherTimer();
        DispatcherTimer timerConnection = new DispatcherTimer();
        BackgroundWorker backgroundConnector = new BackgroundWorker();
        BackgroundWorker backgroundWorkerUpdate = new BackgroundWorker();

        NotifyIcon notifyIcon = new NotifyIcon();
        #endregion

        public ViewModel viewModel = new ViewModel();
        LRMDisplay winLRM;
        static object lockResource = new object();


        public MainWindow()
        {
            bool createdNew = true;
            if (!createdNew)
            {
                System.Windows.MessageBox.Show("App is already running.\nCheck your system tray.", "Gees", MessageBoxButton.OK, MessageBoxImage.Information);
                this.Close();
                return;
            }
            this.DataContext = viewModel;
            InitializeComponent();
            //SYSTEM TRAY
            notifyIcon.Icon = Properties.Resources.icon;
            notifyIcon.Visible = true;
            notifyIcon.MouseClick += notifyIcon_MouseClick;
            //POSITION
            var desktopWorkingArea = System.Windows.SystemParameters.WorkArea;
            this.Left = desktopWorkingArea.Right - this.Width - 10;
            this.Top = desktopWorkingArea.Bottom - this.Height - 10;
            //UPDATER
            backgroundWorkerUpdate.DoWork += backgroundWorkerUpdate_DoWork;
            backgroundWorkerUpdate.RunWorkerAsync();
            //CONNECTIONS
            timerConnection.Interval = new TimeSpan(0, 0, 0, 0, 100);
            timerConnection.Tick += timerConnection_Tick;
            backgroundConnector.DoWork += backgroundConnector_DoWork;
            timerConnection.Start();
            //SIMCONREADER
            timerRead.Interval = new TimeSpan(0, 0, 0, 0, SAMPLE_RATE);
            timerRead.Tick += timerRead_Tick;
            timerBounce.Tick += timerBounce_Tick;
            fsConnect.FsDataReceived += HandleReceivedFsData;
            definition.Add(new SimVar("TITLE", null, SIMCONNECT_DATATYPE.STRING256));
            definition.Add(new SimVar("SIM ON GROUND", "Bool", SIMCONNECT_DATATYPE.INT32));
            definition.Add(new SimVar("AIRCRAFT WIND X", "Knots", SIMCONNECT_DATATYPE.FLOAT64));
            definition.Add(new SimVar("AIRCRAFT WIND Z", "Knots", SIMCONNECT_DATATYPE.FLOAT64));
            definition.Add(new SimVar("AIRSPEED INDICATED", "Knots", SIMCONNECT_DATATYPE.FLOAT64));
            definition.Add(new SimVar("GROUND VELOCITY", "Knots", SIMCONNECT_DATATYPE.FLOAT64));
            definition.Add(new SimVar("VELOCITY BODY X", "Feet per second", SIMCONNECT_DATATYPE.FLOAT64));
            definition.Add(new SimVar("VELOCITY BODY Z", "Feet per second", SIMCONNECT_DATATYPE.FLOAT64));
            definition.Add(new SimVar("G FORCE", "GForce", SIMCONNECT_DATATYPE.FLOAT64));
            definition.Add(new SimVar("PLANE TOUCHDOWN NORMAL VELOCITY", "Feet per second", SIMCONNECT_DATATYPE.FLOAT64));
            //SHOW LRM
            winLRM = new LRMDisplay(viewModel);
            winLRM.Show();
        }

        #region Reading and processing simconnect data
        private void timerRead_Tick(object sender, EventArgs e)
        {
            lock (lockResource)
            {
                if (!ShowLanding)
                {
                    try
                    {
                        fsConnect.RequestData(Requests.PlaneInfo, Requests.PlaneInfo);
                    }
                    catch
                    {
                    }
                }
                else
                {
                    calculateLanding(LandingComplete);
                    //int BOUNCE_TIMER = Properties.Settings.Default.CloseAfterLanding * 1000;
                    //timerBounce.Interval = new TimeSpan(0, 0, 0, 0, BOUNCE_TIMER);
                    //timerBounce.Start();
                    viewModel.LogParams();
                }
            }
        }

        private static void HandleReceivedFsData(object sender, FsDataReceivedEventArgs e)
        {
            if (!SafeToRead)
            {
                Console.WriteLine("lost one");
                Landingdata.Clear();
                ShowLanding = false;
                LandingComplete = false;
                SafeToRead = true;
                return;
            }
            SafeToRead = false;
            try
            {
                if (e.RequestId == (uint)Requests.PlaneInfo)
                {
                    if (!ShowLanding || !LandingComplete)
                    {
                        PlaneInfoResponse r = (PlaneInfoResponse)e.Data.FirstOrDefault();
                        //ignore when noone is flying
                        if (r.ForwardSpeed < 4) //if less then 4kt, it's not a landing or out to menu
                        {
                            SafeToRead = true;
                            return;
                        }
                        lock (lockResource)
                        {
                            Landingdata.Add(r);
                            if (Landingdata.Count > BUFFER_SIZE && !Landingdata.ElementAt(1).OnGround || Landingdata.ElementAt(0).OnGround)
                            {
                                Landingdata.RemoveAt(0);
                            }
                            if (Landingdata.Count > BUFFER_SIZE && Landingdata.ElementAt(1).OnGround && !Landingdata.ElementAt(0).OnGround && (Landingdata.Last().OnGround || Landingdata.Count > BUFFER_SIZE * 2)) //if last not onground, extend the buffer
                            {
                                LandingComplete = true;
                                ShowLanding = true;
                            }
                            else if (Landingdata.Count > BUFFER_SIZE_SHOW && Landingdata.ElementAt(Landingdata.Count - 1 - BUFFER_SIZE_SHOW).OnGround && !Landingdata.ElementAt(0).OnGround) //if last not onground, extend the buffer
                            {
                                LandingComplete = false;
                                ShowLanding = true;
                            }
                        }
                        // POnGround = r.OnGround;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            SafeToRead = true;
        }

        private void calculateLanding(bool landingcomplete)
        {
            //impact calculation
            try
            {
                int landingindex = 0;
                for (; landingindex < Landingdata.Count && !Landingdata.ElementAt(landingindex).OnGround; landingindex++);
                   double fpm = 60 * Landingdata.ElementAt(landingindex).LandingRate;
                Int32 FPM = Convert.ToInt32(-fpm);

                double gees = 0;
                //int Gforcemeterlen = 100 / SAMPLE_RATE; // take 100ms average for G force
                for (int i = 1; i < Landingdata.Count; i++)
                {
                    if (Landingdata.ElementAt(i).OnGround && Landingdata.ElementAt(i).Gforce > gees)
                    {
                        gees = Landingdata.ElementAt(i).Gforce;
                    }
                    /*gees += Onground.ElementAt(i).Gforce;
                    Console.WriteLine(Onground.ElementAt(i).Gforce);*/
                }
               // gees /= BUFFER_SIZE;*/
              //  gees += Onground.ElementAt(0).Gforce;


                double incAngle = Math.Atan(Landingdata.ElementAt(landingindex - 1).LateralSpeed / Landingdata.ElementAt(landingindex - 1).ForwardSpeed) * 180 / Math.PI;

                bounces = 0;
                bool currentonground = false;
                for (int i = 0; i < Landingdata.Count; i++)
                {
                    if (!currentonground && Landingdata.ElementAt(i).OnGround)
                    {
                        currentonground = true;
                    }
                    else if (currentonground && !Landingdata.ElementAt(i).OnGround)
                    {
                        bounces++;
                        currentonground = false;
                    }
                }
                // EnterLog(Inair.First().Type, FPM, gees, Inair.Last().AirspeedInd, Inair.Last().GroundSpeed, Inair.Last().WindHead, Inair.Last().WindLat, incAngle);
                viewModel.SetParams(new ViewModel.Parameters
                {
                    Name = Landingdata.ElementAt(landingindex - 1).Type,
                    FPM = FPM,
                    Gees = Math.Round(gees, 2),
                    Airspeed = Math.Round(Landingdata.ElementAt(landingindex-1).AirspeedInd, 2),
                    Groundspeed = Math.Round(Landingdata.ElementAt(landingindex - 1).GroundSpeed, 2),
                    Crosswind = Math.Round(Landingdata.ElementAt(landingindex - 1).WindLat, 2),
                    Headwind = Math.Round(Landingdata.ElementAt(landingindex - 1).WindHead, 2),
                    Slip = Math.Round(incAngle, 2),
                    Bounces = bounces
                });
                winLRM.SlideLeft();
                // viewModel.UpdateTable();
                //LRMDisplay form = new LRMDisplay(FPM, gees, Inair.Last().AirspeedInd, Inair.Last().GroundSpeed, Inair.Last().WindHead, Inair.Last().WindLat, incAngle);
                //form.Show();
                ShowLanding = false;
                if (landingcomplete)
                {
                    Landingdata.Clear();
                    LandingComplete = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                //some params9 are missing. likely the user is in the main menu. ignore
            }
        }
        private void timerBounce_Tick(object sender, EventArgs e)
        {
            bounces = 0;
            viewModel.LogParams();
            timerBounce.Stop();
        }

        #endregion

        #region Sim Connection
        private void timerConnection_Tick(object sender, EventArgs e)
        {
            if (!backgroundConnector.IsBusy)
                backgroundConnector.RunWorkerAsync();

            if (fsConnect.Connected)
            {
                timerRead.Start();
                notifyIcon.Icon = Properties.Resources.online;
                viewModel.Connected = true;
            }
            else
            {
                notifyIcon.Icon = Properties.Resources.offline;
                viewModel.Connected = false;
            }
        }

        private void backgroundConnector_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            if (!fsConnect.Connected)
            {
                try
                {
                    fsConnect.Connect("TestApp", "localhost", 500, SimConnectProtocol.Ipv4);
                    fsConnect.RegisterDataDefinition<PlaneInfoResponse>(Requests.PlaneInfo, definition);
                }
                catch { } // ignore
            }
        }
        #endregion

        #region Handlers for UI
        private void button_Click(object sender, RoutedEventArgs e)
        {
           // notifyIcon.Visible = false;
            Properties.Settings.Default.Save();
            notifyIcon.Visible = false;
            Environment.Exit(1);
        }
        private void button_Hide_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }
        private void redditLink_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Process.Start("https://www.reddit.com/r/MSFS2020LandingRate/");
        }
        private void githubLink_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Process.Start("https://github.com/scelts/gees");
        }
        private void buttonUpdate_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(updateUri);
        }
        private void buttonLandings_Click(object sender, RoutedEventArgs e)
        {
            LandingsWindow winland = new LandingsWindow(viewModel);
            winland.Show();
        }
        private void buttonTest_Click(object sender, RoutedEventArgs e)
        {
            winLRM.SlideLeft();
        }
        private void textBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (int.TryParse(textBox.Text, out _))
            {
                Properties.Settings.Default.Save();
            }
            else
            {
                e.Handled = true;
            }
        }

        private void checkBox_Checked(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.Save();
        }

        private void textBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (int.TryParse(e.Text, out _))
            {
            }
            else
            {
                e.Handled = true;
            }
        }
        private void comboBoxScreens_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Properties.Settings.Default.Save();
        }
        #endregion

        #region Logging and data handling
    /*    void MakeLogIfEmpty()
        {
            string ls = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ListSeparator;
            const string header = "Time,Plane,FPM,Impact (G),Air Speed (kt),Ground Speed (kt),Headwind (kt),Crosswind (kt),Sideslip (deg)";
            string myDocs = System.Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Directory.CreateDirectory(myDocs + @"\MyMSFS2020Landings-Gees"); //create if doesn't exist
            string path = myDocs + @"\MyMSFS2020Landings-Gees\Landings.v1.csv";
            if (!File.Exists(path))
            {
                using (StreamWriter w = File.CreateText(path))
                {
                    w.WriteLine(header);
                }
            }
        }
        void EnterLog(string Plane, int FPM, double G, double airV, double groundV, double headW, double crossW, double sideslip)
        {

            MakeLogIfEmpty();
            string myDocs = System.Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string path = myDocs + @"\MyMSFS2020Landings-Gees\Landings.v1.csv";
            using (StreamWriter w = File.AppendText(path))
            {
                string logLine = DateTime.Now.ToString("G") + ",";
                logLine += Plane + ",";
                logLine += FPM + ",";
                logLine += G.ToString("0.##") + ",";
                logLine += airV.ToString("0.##") + ",";
                logLine += groundV.ToString("0.##") + ",";
                logLine += headW.ToString("0.##") + ",";
                logLine += crossW.ToString("0.##") + ",";
                logLine += sideslip.ToString("0.##");
                w.WriteLine(logLine);
            }
        }*/
        #endregion

        #region System Tray handling
        private void notifyIcon_MouseClick(object sender, MouseEventArgs e)
        {
            if (lastDeactivateValid && Environment.TickCount - lastDeactivateTick < 1000) return;
            this.Show();
            this.Activate();
        }
        private void Window_Deactivated(object sender, EventArgs e)
        {
            lastDeactivateTick = Environment.TickCount;
            lastDeactivateValid = true;
            this.Hide();
        }
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
        #endregion

        #region Updater
        private void backgroundWorkerUpdate_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            var client = new GitHubClient(new ProductHeaderValue("Gees"));
            var releases = client.Repository.Release.GetAll("scelts", "gees").Result;
            var latest = releases[0];
            viewModel.Updatable = viewModel.Version != latest.TagName;
            updateUri = latest.HtmlUrl;
        }

        #endregion
    }
}
