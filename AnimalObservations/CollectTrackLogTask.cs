﻿using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ESRI.ArcGIS.Mobile.Client;
using ESRI.ArcGIS.Mobile.Geometries;
using ESRI.ArcGIS.Mobile.Gps;
using ESRI.ArcGIS.Mobile.WPF;
using MessageBox = ESRI.ArcGIS.Mobile.Client.Windows.MessageBox;
using Point = System.Drawing.Point;

//TODO - explore using ESRI.ArcGIS.Mobile.WPF.GpsDisplay
//TODO - using locking so GPS events can be processed correctly on the GPS thread.

namespace AnimalObservations
{
    public class CollectTrackLogTask : Task
    {

        #region Constructor
        
        public CollectTrackLogTask()
        {
            //Note: The constructor is called on a background thread before the UI maybe ready,
            //so we can't set UI elements like the ImageSource (task icon) property.  Use GetImageSource() override
            Name = "Collect Observations";  
            Description = "Begin data logging along a transect";

            NearbyTransects = new ObservableCollection<Transect>();

            MobileApplication.Current.ProjectClosing += (s, e) => CloseProject();
        }

        private void CloseProject()
        {
            CloseGpsConnection();
            StopRecording();
        }

        #endregion


        #region Overrides

        protected override ImageSource GetImageSource()
        {
            var uri = new Uri("pack://application:,,,/AnimalObservations;Component/duck-icon.png");
            return new BitmapImage(uri);
        }

        protected override void OnOwnerInitialized()
        {
            //Note: This is called on a background non-STA thread,
            //Which causes some problems, so we divert it onto the main thread.
            if (!MobileApplication.Current.Dispatcher.CheckAccess())
            {
                MobileApplication.Current.Dispatcher.Invoke(new Action(OnOwnerInitialized));
                return;
            }
            ValidateDatabaseSchema();
            if (DatabaseSchemaIsInvalid)
                return;
            InitializeObservationQueue();
            //Must set up the boat layer before the GPS connection; gps events assume boat is ready to draw.
            SetupBoatLayer();
            InitializeGpsConnection();
            AutoPan = true;
        }

        public override void Execute()
        {
            //Tasks can always be executed - no override for CanExecute() - so check valididty here.
            if (DatabaseSchemaIsInvalid)
                return;

            var mapExtents = MobileApplication.Current.Map.Extent;
            if (mapExtents == null)
            {
                MessageBox.ShowDialog(
                    "Unable to determine extents of the map.", "Unexpected Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            RefreshNearbyTransects();
            if (NearbyTransects.Count < 1)
            {
                MessageBox.ShowDialog(
                    "No transects could be found within the visible map. " +
                    "Either zoom out or wait until a transect comes into view. " + 
                    "Make sure the GPS is on and the map is tracking your location.", "No Transects Found",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
#if TESTINGWITHOUTGPS
            ESRI.ArcGIS.Mobile.Client.Windows.MessageBox.ShowDialog("Program is operating without a GPS.", "Test Mode");
#else
            if (!_gpsConnection.IsOpen || MostRecentLocation == null)
            {
                MessageBox.ShowDialog(
                    "GPS is disconnected or doesn't yet have a fix on the satellites. " +
                    "Correct the problems with the GPS and try again.", "No GPS Fix",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
#endif
            MobileApplication.Current.Transition(new SetupTrackLogPage());
        }

        #endregion


        #region Manage Transects

        //Use a public Observable Collection for XAML Binding
        public ObservableCollection<Transect> NearbyTransects { get; private set; }

        //The list starts out empty, and we cannot begin observations if there are no nearby transects.

        internal void RefreshNearbyTransects()
        {
            Envelope mapExtents = MobileApplication.Current.Map.Extent;
            bool transectsAreNearby = (Transect.GetWithin(mapExtents).FirstOrDefault() != null);
            //Leave the list unchanged if there are no nearby transects.
            //We don't want the pick list to go blank if we are changing tracklog properties in the
            //middle of a transect, but we happen to be zoomed in and can't see the transect.
            if (transectsAreNearby)
            {
                NearbyTransects.Clear();
                foreach (var transect in Transect.GetWithin(mapExtents))
                    NearbyTransects.Add(transect);
            }
        }

        #endregion


        #region Manage TrackLog Recording

        //If CurrentTrackLog == null then we are not recording.
        //while current tracklog is not null recording may be turned on/off.
        //CurrentTrackLog will never be changed unless recording is off.

        internal void StartRecording()
        {
#if TESTINGWITHOUTGPS
            //Create a bogus GPS location
            CurrentGpsPoint = GpsPoint.FromGpsConnection(CurrentTrackLog, _gpsConnection);
            //MostRecentLocation = new Coordinate(448262, 6479766);  //Main dock
            MostRecentLocation = new Coordinate(443759, 6484291);  //East end of MainBay19
            IsRecording = true;
#else
            if (CurrentTrackLog == null || !_gpsConnection.IsOpen)
            {
                IsRecording = false;
            }
            else
            {
                IsRecording = true;
                CurrentTrackLog.StartingTime = DateTime.Now;
                //Collect our first point now, don't wait for an event. 
                SaveGpsPoint();
                //At this time the tracklog geometry is invalid, so we don't bother saving it.
                //The tracklog will save itself as soon as it has two points. 
                CurrentTrackLog.PropertyChanged +=
                    (sender, args) => { if (args.PropertyName == "TooBig") RestartTrackLog(); };
            }
#endif
        }

        internal void StopRecording()
        {
            if (!IsRecording) return;

            //Stop recording is currently called in the following situations:
            // 1) if the project is closing - could happen at any time
            // 2) if there is a GPS close event - could happen at any time
            // 3) when the record tracklog page clicks the stop button
            // In case 1, the host will handle the page transition to the open project page
            // In case 2, the event will transition to the project start page (map page)
            // In case 3, the UI will handle the page transitions to the previous page

#if !TESTINGWITHOUTGPS
            //Save any outstanding changes
            if (!PostChanges())
                MessageBox.ShowDialog("Error saving changes", "Save Failed");
#endif
            //Close any open observations
            while (ActiveObservation != null)
                OpenObservations.Remove(ActiveObservation);

            IsRecording = false;
            DefaultTrackLog = CurrentTrackLog;
            CurrentTrackLog = null;
        }

        private bool IsRecording { get; set;}

#if !TESTINGWITHOUTGPS
        private bool PostChanges()
        {
            //Save() may throw exceptions, but that would be catastrophic, so let the app handle it.
            bool saved = CurrentTrackLog.Save();
            foreach (var observation in OpenObservations)
                saved = saved && observation.Save();
            return saved;
        }
#endif

        internal TrackLog DefaultTrackLog { get; set; }

        //Use INotifyPropertyChanged to keep UI up to date
        public TrackLog CurrentTrackLog
        {
            get { return _currentTrackLog; }
            set
            {
                if (IsRecording)
                    throw new InvalidOperationException("Cannot change current track log while recording.");
                if (value != _currentTrackLog)
                {
                    _currentTrackLog = value;
                    OnPropertyChanged("CurrentTrackLog");
                }
            }
        }
        private TrackLog _currentTrackLog;

        private void RestartTrackLog()
        {
            var newTracklog = TrackLog.FromTrackLog(CurrentTrackLog);
            StopRecording();
            CurrentTrackLog = newTracklog;
            StartRecording();
        }

        #endregion


        #region Manage Observation Queue

        //public for XAML
        public ObservableCollection<Observation> OpenObservations { get; private set; }

        //public for XAML
        public Observation ActiveObservation
        {
            get { return _activeObservation; }
            set
            { 
                if (value != _activeObservation)
                {
                   _activeObservation = value;
                    OnPropertyChanged("ActiveObservation");
                }
            }
        }
        private Observation _activeObservation;

        private void InitializeObservationQueue()
        {
            OpenObservations = new ObservableCollection<Observation>();
            ActiveObservation = null;
        }

        public void AddObservationAsActive(Observation observation)
        {
            OpenObservations.Add(observation);
            ActiveObservation = observation;
        }

        public void RemoveObservation(Observation observation)
        {
            OpenObservations.Remove(observation);
            //If we are deleting the active record, make the first record active
            //If observation == ActiveObservation, then XAML bindings will set ActiveObservation to null
            if (ActiveObservation == null || observation == ActiveObservation)
                ActiveObservation = (OpenObservations.Count == 0) ? null : OpenObservations[0];
        }

        #endregion


        #region Manage GPS connection and events

        //ALL GPS EVENTS HAPPEN ON A BACKGROUND THREAD
        //in lieu of locking I am routing them onto the UI thread.  This is safe but inefficient.

        /// <summary>
        /// This is updated only when we are recording.  These objects are saved to disk
        /// </summary>
        public GpsPoint CurrentGpsPoint { get; private set; }

        /// <summary>
        /// This is updated whenever the GPS is receiving a position fix.  It is only used for map updates/queries
        /// </summary>
        public Coordinate MostRecentLocation { get; private set; }

        private GpsConnection _gpsConnection;

        private void InitializeGpsConnection()
        {
            HookupGpsEvents();
        }

        private void CloseGpsConnection()
        {
            UnhookGpsEvents();
            _gpsConnection = null;
        }

        private void UnhookGpsEvents()
        {
            if (_gpsConnection == null)
                return;
            _gpsConnection.GpsClosed -= ProcessGpsClosedEventFromConnection;
            _gpsConnection.GpsError -= ProcessGpsErrorEventFromConnection;
            _gpsConnection.GpsChanged -= ProcessGpsChangedEventFromConnection;
            HideBoat();
        }

        private void HookupGpsEvents()
        {
            if (_gpsConnection == null)
                _gpsConnection = MobileApplication.Current.GpsConnectionManager.Connection;
            if (_gpsConnection.IsOpen)
            {
                _gpsConnection.GpsClosed += ProcessGpsClosedEventFromConnection;
                _gpsConnection.GpsError += ProcessGpsErrorEventFromConnection;
                _gpsConnection.GpsChanged += ProcessGpsChangedEventFromConnection;
                _gpsConnection.GpsOpened -= ProcessGpsOpenedEventFromConnection;
                ShowBoat();
            }
            else
            {
                _gpsConnection.GpsOpened += ProcessGpsOpenedEventFromConnection;                
            }
        }

        void ProcessGpsOpenedEventFromConnection(object sender, EventArgs e)
        {
            if (MobileApplication.Current.Dispatcher.CheckAccess())
                HookupGpsEvents();
            else
                MobileApplication.Current.Dispatcher.Invoke(new Action(HookupGpsEvents));
        }

        //This event is raised then an exception is encountered during processing GPS data
        //I have yet to see this happen, so I'm not sure how to correctly respond.
        //Until I have better information, I will respond by closing the GPS connection.
        //This should trigger the gps closed event so I can save any work in progress.
        void ProcessGpsErrorEventFromConnection(object sender, GpsErrorEventArgs e)
        {
            if (!MobileApplication.Current.Dispatcher.CheckAccess())
            {
                MobileApplication.Current.Dispatcher.Invoke(
                    (Action) (() => ProcessGpsErrorEventFromConnection(sender, e)));
                return;
            }

            MessageBox.ShowDialog(
                "An error (" + e.Exception.Message + ") was encountered processing the GPS data. " +
                "Data has been saved and the GPS has been closed.",
                "GPS Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _gpsConnection.Close();
        }

        //The closed event is generated if the connection is lost: i.e. no power, cable unplugged, etc.
        void ProcessGpsClosedEventFromConnection(object sender, EventArgs e)
        {
            if (!MobileApplication.Current.Dispatcher.CheckAccess())
            {
                MobileApplication.Current.Dispatcher.Invoke(
                    (Action)(() => ProcessGpsClosedEventFromConnection(sender, e)));
                return;
            }

            UnhookGpsEvents();
            _gpsConnection.GpsOpened += ProcessGpsOpenedEventFromConnection;
            if (IsRecording)
            {
                StopRecording();
            }
            MobileApplication.Current.Transition(MobileApplication.Current.Project.HomePage);
        }

        void ProcessGpsChangedEventFromConnection(object sender, EventArgs e)
        {
            if (_gpsConnection == null)
                //This only happens in odd cases where we are starting up/shutting down and the threads are get out of sync.
                return;
            if (!LocationChanged(_gpsConnection))
                return;

            if (!MobileApplication.Current.Dispatcher.CheckAccess())
            {
                MobileApplication.Current.Dispatcher.Invoke(
                    (Action) (() => ProcessGpsChangedEventFromConnection(sender, e)));
                return;
            }

            if (_gpsConnection.FixStatus == GpsFixStatus.Invalid)
            {
                DrawBoat(MostRecentLocation, _gpsConnection.Course, true);
                return;
            }

            if (IsRecording)
            {
                SaveGpsPoint();
            }
            else
            {
                double latitude = _gpsConnection.Latitude;
                double longitude = _gpsConnection.Longitude;
#if GPSINANCHORAGE
                //Offset Regan's office to end of Transect 001
                latitude -= (61.2174 - 58.5260);
                longitude += (149.88493 - 135.9615);
#elif GPSINJUNEAU
                //Offset SEAN Juneau office to end of Transect 001
                latitude -= (58.377663888 - 58.5260);
                longitude += (134.69872777 - 135.9615);
#endif
                MostRecentLocation = MobileApplication.Current.Project.SpatialReference.FromGps(longitude, latitude);
            }
            if (OpenObservations.Count == 0)
                DrawBoat(MostRecentLocation, _gpsConnection.Course, false);
        }

        private void SaveGpsPoint()
        {
            //CreateWith() and Save() may throw exceptions, but that would be catastrophic, so let the app handle it.
            CurrentGpsPoint = GpsPoint.FromGpsConnection(CurrentTrackLog, _gpsConnection);
            if (CurrentGpsPoint.Save())
                CurrentTrackLog.AddPoint(CurrentGpsPoint.Location);
            MostRecentLocation = CurrentGpsPoint.Location;
        }

        private static bool LocationChanged(GpsConnection gpsConnection)
        {
            return (gpsConnection.GpsChangeType & GpsChangeType.Position) != 0;
        }

        #endregion


        #region Draw the boat

        public bool AutoPan { get; set; }

        private Image _boat;
        private GraphicLayer _overlay;

        private void SetupBoatLayer()
        {
            //Get Boat Icon
            var uri = new Uri("pack://application:,,,/AnimalObservations;Component/Boat-icon.png");
 
            //Creating a new Image must be done on a STA thread.  The background thread that calls
            //OnOwnerInitialized() is not STA
            _boat = new Image
                        {
                            Source = new BitmapImage(uri), 
                            Margin = new Thickness(-100, -100, 0, 0)
                        };

            // add graphic layer
            _overlay = new GraphicLayer();
            MobileApplication.Current.Map.MapGraphicLayers.Add(_overlay);
        }

        private void ShowBoat()
        {
            _overlay.Children.Add(_boat);
        }

        private void HideBoat()
        {
            _overlay.Children.Remove(_boat);
        }

        private void DrawBoat(Coordinate location, double heading, bool error)
        {

            _boat.Opacity = error ? .45 : 1;

            //pan map to center boat location
            if (AutoPan)
            {
                Envelope env = MobileApplication.Current.Map.Extent;
                env = env.Resize(0.7);
                if (!env.Contains(location))
                    MobileApplication.Current.Map.CenterAt(location);
            }

            //heading = 0 is north (up); heading is in degrees clockwise
            //image without rotation is pointing E (270 degrees).
            Point point = MobileApplication.Current.Map.ToClient(location);
            if (heading > 180)
                _boat.LayoutTransform = new RotateTransform(heading + 90.0);
            else
            {
                var transformGroup = new TransformGroup();
                transformGroup.Children.Add(new ScaleTransform(-1.0, 1.0));
                transformGroup.Children.Add(new RotateTransform(heading - 90.0));
                _boat.LayoutTransform = transformGroup;
            }
            _boat.Margin = new Thickness(point.X-24, point.Y-30, 0, 0);
        }

        #endregion


        #region Test database Schema

        private void ValidateDatabaseSchema()
        {
            _validDb = !TestDatabaseSchema();
        }

        private bool DatabaseSchemaIsInvalid
        {
            get
            {
                if (!_validDb.HasValue)
                    _validDb = !TestDatabaseSchema();
                return _validDb.Value;
            }
        }
        private bool? _validDb;

        private static bool TestDatabaseSchema()
        {
            const string errorMsg = "The structure of the database has been\n" +
                                    "modified. Field work cannot proceed\n" +
                                    "until the database is restored to\n" +
                                    "normal, or the application is updated.\n" +
                                    "Details:\n";
            try
            {
                //Initializes the transect Class
                Transect transect = Transect.AllTransects.FirstOrDefault();
                if (transect == null)
                {
                    MessageBox.ShowDialog(
                        "Field work cannot proceed until\n" +
                        "transects are downloaded to the\n" +
                        "mobile cache.",
                        "No Transects Found",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
                //Initialize other classes
                TrackLog trackLog = TrackLog.FromTransect(transect);
                GpsPoint gpsPoint = GpsPoint.FromTrackLog(trackLog);
                Observation observation = Observation.FromGpsPoint(gpsPoint);
                BirdGroupFeature birdGroupFeature = BirdGroupFeature.FromObservation(observation);
                //Unwind - destroy the temporary objects
                birdGroupFeature.Delete();
                observation.Delete();
                gpsPoint.Delete();
                trackLog.Delete();
                return true;
            }
            catch (TypeInitializationException ex)
            {
                MessageBox.ShowDialog(
                    errorMsg + ex.InnerException.Message,
                    "Invalid Database",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.ShowDialog(
                    errorMsg + ex.Message,
                    "Invalid Database",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        #endregion

    }
}
