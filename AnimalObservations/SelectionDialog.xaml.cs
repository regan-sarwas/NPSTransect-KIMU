﻿using System.Windows;

namespace AnimalObservations
{
    public partial class SelectionDialog
    {
        public SelectionAction Action { get; private set; }

        public SelectionDialog(Observation observation)
        {
            Observation = observation;
            InitializeComponent();
        }

        public Observation Observation { get; private set; }

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            Action = SelectionAction.Edit;
            Close();
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            Action = SelectionAction.Delete;
            Close();
        }

    }

    public enum SelectionAction
    {
        Nothing,
        Delete,
        Edit
    }

}
