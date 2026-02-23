using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Toolbox.Library.Forms;
using Octokit;

namespace Toolbox
{
    public partial class GithubUpdateDialog : STForm
    {
        public GithubUpdateDialog()
        {
            InitializeComponent();
        }

        private List<GitHubCommit> ActiveCommitList;
        private List<UpdatePatchNote> ActivePatchNotes;

        public void LoadCommits(List<GitHubCommit> commits)
        {
            ActivePatchNotes = null;
            ActiveCommitList = commits ?? new List<GitHubCommit>();
            listViewCustom1.Items.Clear();
            stTextBox1.Text = string.Empty;
            stLabel1.Text = "Updates are found! Would you like to update to the latest?";

            foreach (var commit in ActiveCommitList)
                listViewCustom1.Items.Add(commit.Commit.Message).SubItems.Add(commit.Commit.Author.Date.LocalDateTime.ToString());

            if (listViewCustom1.Items.Count > 0)
                listViewCustom1.Items[0].Selected = true;
        }

        public void LoadPatchNotes(string releaseTitle, List<UpdatePatchNote> notes)
        {
            ActiveCommitList = null;
            ActivePatchNotes = notes ?? new List<UpdatePatchNote>();
            listViewCustom1.Items.Clear();
            stTextBox1.Text = string.Empty;

            string titleText = string.IsNullOrWhiteSpace(releaseTitle) ? "latest release" : releaseTitle;
            stLabel1.Text = $"Update found: {titleText}. Update now?";

            foreach (var note in ActivePatchNotes)
                listViewCustom1.Items.Add(note.Summary).SubItems.Add(note.Date ?? string.Empty);

            if (listViewCustom1.Items.Count > 0)
                listViewCustom1.Items[0].Selected = true;
        }

        private void listViewCustom1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (listViewCustom1.SelectedIndices.Count <= 0)
                return;

            int index = listViewCustom1.SelectedIndices[0];

            if (ActivePatchNotes != null && index < ActivePatchNotes.Count)
            {
                stTextBox1.Text = ActivePatchNotes[index].Details;
                return;
            }

            if (ActiveCommitList != null && index < ActiveCommitList.Count)
                stTextBox1.Text = ActiveCommitList[index].Commit.Message;
        }
    }
}
