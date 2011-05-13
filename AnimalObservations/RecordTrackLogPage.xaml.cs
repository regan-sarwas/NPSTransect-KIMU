﻿using System;
using System.Diagnostics;
using System.Windows.Input;
using ESRI.ArcGIS.Mobile.Client;
using ESRI.ArcGIS.Mobile.Geometries;

namespace AnimalObservations
{
    public partial class RecordTrackLogPage
    {

        private readonly CollectTrackLogTask _task;
        private readonly TrackLog _trackLog;

        #region Constructor

        public RecordTrackLogPage()
        {
            _task = MobileApplication.Current.FindTask(typeof(CollectTrackLogTask)) as CollectTrackLogTask;
            Debug.Assert(_task != null, "Fail!, Task is null in RecordTrackLogPage");
            _trackLog = _task.CurrentTrackLog;

            InitializeComponent();

            //Page Captions
            Title = "Transect " + _trackLog.Transect.Name;
            Note = "Capturing GPS points in track log";

            // page icon
            var uri = new Uri("pack://application:,,,/AnimalObservations;Component/duck-icon.png");
            ImageSource = new System.Windows.Media.Imaging.BitmapImage(uri);

            // back button
            CancelCommand.Text = "Stop Recording";
            BackCommands.Add(CancelCommand);

            // forward Button
            OkCommand.Text = "New Observation";
            ForwardCommands.Add(OkCommand);

            //Setup desired keyboard behavior
            Focusable = true;

            //do some initialization each time the page is displayed
            Loaded += (s, e) => Keyboard.Focus(this);
        }

        #endregion

        #region Page navigation overrides

        protected override void OnCancelCommandExecute()
        {
            _task.StopRecording();
            MobileApplication.Current.Transition(PreviousPage);
        }

        protected override void OnOkCommandExecute()
        {
            //Log observation point and open observation attribute page

            //We create the observation at the last GPS point.
            //We do not wait for the next GPS point to interpolate, because
            //1) Observations have a 1 to 1 relationship with GPS points,
            //2) we may never get a next GPS point,
            //3) There is a lag in communication between observers and recorders,
            //   therefore the actual point of observation is closer to the last GPS point
            //   than an interpolated point base on when this operation runs.

            //FIXME - Task.CurrentGpsPoint may be null if this thread caught the main thread between states.
            //It only happens on occasion, but enough to be annoying.
            Debug.Assert(_task.CurrentGpsPoint != null, "Fail! Current GPS Point is null when recording an observation.");
            if (_task.CurrentGpsPoint == null)
                return;
            //TODO - add try/catch - CreateWith() may throw exceptions
            Observation observation = Observation.CreateWith(_task.CurrentGpsPoint);
            _task.AddObservation(observation);
            MobileApplication.Current.Transition(new EditObservationAttributesPage());
        }

        #endregion

        #region Mouse event overrides

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            //get the location of the mouse down event for use by the mouse up event
            _mouseDownPoint = e.GetPosition(this);
            base.OnMouseDown(e);
        }
        private System.Windows.Point _mouseDownPoint;

        //TODO drag image while panning

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            System.Windows.Point mouseUpPoint = e.GetPosition(this);
            var drawingPoint = new System.Drawing.Point(Convert.ToInt32(mouseUpPoint.X), Convert.ToInt32(mouseUpPoint.Y));
            
            //Check to see if there is an observation related to this location.  If so - edit it, then be done
            Observation observation = GetObservation(drawingPoint);
            if (observation != null)
            {
                _task.AddObservation(observation);
                MobileApplication.Current.Transition(new EditObservationAttributesPage());
                e.Handled = true;
                return;
            }

            //Check to see if the mouseup location is substantially different than the mouse down location, if so then pan and be done
            int dx = Convert.ToInt32(mouseUpPoint.X - _mouseDownPoint.X);
            int dy = Convert.ToInt32(mouseUpPoint.Y - _mouseDownPoint.Y);

            if (dx < -5 || 5 < dx || dy < -5 || 5 < dy)
                mapControl.Map.Pan(dx, dy);

            //otherwise, do default behavior
            base.OnMouseUp(e);
        }

        //May return null if no observation is found
        private static Observation GetObservation(System.Drawing.Point drawingPoint)
        {
            Coordinate mapPoint = MobileApplication.Current.Map.ToMap(drawingPoint);
            var extents = new Envelope(mapPoint, MobileUtilities.SearchRadius * 2, MobileUtilities.SearchRadius * 2);
            Observation observation = null;
            try
            {
                //Try and find a bird group in this extent
                BirdGroup birdGroup = null;
                try
                {
                    birdGroup = BirdGroup.FromEnvelope(extents);
                }
                //TODO - be more descriminating on exceptions, and re-throw where appropriate.
                catch (Exception ex)
                {
                    Trace.TraceError("Caught and ignored exception {0}", ex.Message);
                    //Allow birdGroup to default to null, i.e. no birdGroup found in extents
                }
                if (birdGroup != null)
                    observation =  birdGroup.Observation;

                //We did not find a birdgroup, so try and find an observation in this extent
                //Check to see if it is in our cache, if not, then load from database
                if (observation == null)
                    observation = Observation.FromEnvelope(extents);
            }
            //TODO - be more descriminating on exceptions, and provide error message where appropriate.
            catch (Exception ex)
            {
                Trace.TraceError("Caught and ignored exception {0}", ex.Message);
                //Allow observation to default to null, i.e. no observation found in extents
            }
            return observation;
        }

        #endregion

        #region Keyboard event overrides

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter ||
                (e.Key == Key.S && e.KeyboardDevice.Modifiers == ModifierKeys.Control))
            {
                e.Handled = true;
                OnOkCommandExecute();
                return;
            }
            if (e.Key == Key.Escape ||
                (e.Key == Key.W && e.KeyboardDevice.Modifiers == ModifierKeys.Control))
            {
                e.Handled = true;
                OnCancelCommandExecute();
                return;
            }
            base.OnKeyDown(e);
        }
        #endregion
    }
}
