﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Timers;
using System.Diagnostics;
using System.Threading;

using WiiTUIO.Provider;
using WiiTUIO.Output;
using HidLibrary;

/*
 * This code is based on code in the MulitTouch.Driver.Logic namespace provided with the MultiTouchVista project.
 * http://multitouchvista.codeplex.com/
 * License: http://multitouchvista.codeplex.com/license
 */


namespace WiiTUIO.WinTouch
{
    /// <summary>
    /// This class forwards WiiProvider events to the windows stack.
    /// </summary>
    public class MTVProviderHandler : ITouchProviderHandler
    {

        public static bool HasDriver() {
            IEnumerable<HidDevice> devices = HidDevices.Enumerate(0xdddd, 0x0001);
            return devices.FirstOrDefault() != null;
        }


        #region IProviderHandler
        public event Action OnConnect;

        public event Action OnDisconnect;

        public void processEventFrame(FrameEventArgs e)
        {
            // For every contact in the list of contacts.
            foreach (WiiContact pContact in e.Contacts)
            {
                // Construct a new HID frame based on the contact type.
                switch (pContact.Type)
                {
                    case ContactType.Start:
                        this.enqueueContact(HidContactState.Adding, pContact);
                        break;
                    case ContactType.Move:
                        this.enqueueContact(HidContactState.Updated, pContact);
                        break;
                    case ContactType.End:
                        this.enqueueContact(HidContactState.Removing, pContact);
                        break;
                }
            }

            // Flush the contacts?
            this.sendContacts();
        }


        public void connect()
        {
            // Access the HID device driver.
            IEnumerable<HidDevice> devices = HidDevices.Enumerate(0xdddd, 0x0001);
            this.pDevice = devices.FirstOrDefault();
            if (this.pDevice == null)
                throw new InvalidOperationException("Universal Software HID driver was not found. Please ensure that it is installed.");
            this.pDevice.OpenDevice(HidDevice.DeviceMode.Overlapped, HidDevice.DeviceMode.NonOverlapped);
            OnConnect();
        }

        public void disconnect()
        {
            if(this.pDevice != null)
                this.pDevice.Dispose();
            this.pDevice = null;
            OnDisconnect();
        }

        public void showSettingsWindow()
        {
            ;
        }

        #endregion

        #region HID Device Properties
        /// <summary>
        /// A list of the contacts currently queued up.
        /// </summary>
        private Queue<HidContactInfo> lCurrentContacts = null;

        /// <summary>
        /// A table of currently active contacts.
        /// </summary>
        private Dictionary<int, HidContactInfo> dLastContacts = null;

        /// <summary>
        /// A reference to a human interface device driver we want to send data too.
        /// </summary>
        private HidDevice pDevice;

        /// <summary>
        /// A class which we use to compare things in the table.
        /// </summary>
        private HidContactInfoEqualityComparer pComparer;

        /// <summary>
        /// A way to provide mutual exclusion to the current contacts list.
        /// </summary>
        private Mutex pContactLock;
        #endregion

        /// <summary>
        /// Construct a new provider handler class.
        /// </summary>
        public MTVProviderHandler()
        {
            // The comparer.
            this.pComparer = new HidContactInfoEqualityComparer();

            // Create the contact list and tables.
            this.lCurrentContacts = new Queue<HidContactInfo>();
            this.dLastContacts = new Dictionary<int, HidContactInfo>();
            this.pContactLock = new Mutex();
        }

        /// <summary>
        /// Do we have a connection to the HID device.
        /// </summary>
        /// <returns></returns>
        public bool isConnected()
        {
            if (this.pDevice == null)
                return false;
            return this.pDevice.IsConnected;
        }

        /// <summary>
        /// Do we have an open connection to the HID device.
        /// </summary>
        /// <returns></returns>
        public bool isOpen()
        {
            if (this.pDevice == null)
                return false;
            return this.pDevice.IsOpen;
        }

        /// <summary>
        /// Enqueue a new contact into the current list.
        /// </summary>
        /// <remarks>Yes I know it is horrible and iccky to do this here.. but yea..  I didn't want to expose too much of the HID stuff at this stage.</remarks>
        /// <param name="eState">The HidContactState of the contact.</param>
        /// <param name="pContact">The reference to the WiiContact generated by the provider.</param>
        public void enqueueContact(HidContactState eState, WiiContact pContact)
        {
            this.enqueueContact(new HidContactInfo(eState, pContact));
        }

        /// <summary>
        /// This is called to enqueue a HID contact onto the list to send out.
        /// </summary>
        /// <param name="contactInfo"></param>
        private void enqueueContact(HidContactInfo pContact)
        {
            // Obtain mutual exclusion over the queue.
            this.pContactLock.WaitOne();
            
            // Add it to the queue.
            this.lCurrentContacts.Enqueue(pContact);

            // Release exclusion.
            this.pContactLock.ReleaseMutex();
        }

        /// <summary>
        /// Calling this will send all the currently queued contacts off to the HID driver.
        /// </summary>
        public void sendContacts()
        {
            // Build the list of contacts to send.
            List<HidContactInfo> lContacts = new List<HidContactInfo>();

            // Get mutual exclusion over the queue.
            this.pContactLock.WaitOne();

            // While there are still contacts to deal with...
            while (this.lCurrentContacts.Count > 0)
            {
                // Pop the one off the start of the queue.
                HidContactInfo pContact = this.lCurrentContacts.Dequeue();

                // If there is a previous contact it had a remove signal then we want to ignore the event.
                HidContactInfo pPreviousContact;
                if (this.dLastContacts.TryGetValue(pContact.Id, out pPreviousContact))
                {
                    // If we got an update for the new contact and a remove in the previous state then drop the contact.
                    if (pContact.State == HidContactState.Updated && pPreviousContact.State == HidContactState.Removing)
                        continue;
                }

                // Update the contact list to contain the latest reference.
                this.dLastContacts[pContact.Id] = pContact;

                // Append it to the list of contacts we want to ship out.
                lContacts.Add(pContact);
            }

            // Release access to the queue.
            this.pContactLock.ReleaseMutex();

            // Add all existing contacts in the table to the send-list which are not already in there and set to update. 
            lContacts.AddRange(this.dLastContacts.Values.Except(lContacts, this.pComparer).Where(c => c.State == HidContactState.Updated).ToList());

            // If we have more than one contact after all that...
            if (lContacts.Count > 0)
                this.sendContacts(lContacts);

            // Remove all active contacts which are flagged as removed from the table.
            // TODO: Perhaps put a watchdog do to this but with a timeout?
            foreach (ushort id in this.dLastContacts.Values.Where(c => c.State == HidContactState.Removed).Select(c => c.Id).ToList())
                this.dLastContacts.Remove(id);

            // Queue up all contacts which are flagged as removing and send a remove event.
            foreach (HidContactInfo pContact in this.dLastContacts.Values.Where(c => c.State == HidContactState.Removing).ToList())
                this.enqueueContact(new HidContactInfo(HidContactState.Removed, pContact.Contact));

            // Queue up all contacts which are flagged as added and send an updated event.
            foreach (HidContactInfo pContact in this.dLastContacts.Values.Where(c => c.State == HidContactState.Adding).ToList())
                this.enqueueContact(new HidContactInfo(HidContactState.Updated, pContact.Contact));
        }

        /// <summary>
        /// A helper method that wraps up a list of contacts into multitouch reports before sending them out.
        /// </summary>
        /// <param name="lContacts">The list of contacts to send.</param>
        private void sendContacts(List<HidContactInfo> lContacts)
        {
            
            // Create a new report.
            MultiTouchReport pReport = new MultiTouchReport((byte)lContacts.Count, true);
            int iProcessedContacts = 0;

            // For each contact to send.
            foreach (HidContactInfo pContact in lContacts)
            {
                // Update the timestamp and increment the report contact index.
                pContact.Timestamp = DateTime.Now;
                ++iProcessedContacts;

                // Add the contact to the report.
                pReport.addContact(pContact);

                // If we have finished processing all the contacts.
                if (lContacts.Count - iProcessedContacts == 0)
                {
                    // Ship the report out.
                    this.sendReport(pReport);
                }

                // Otherwise if we have reached the maxiumum contacts per report count.
                else if (iProcessedContacts % MultiTouchReport.MaxContactsPerReport == 0)
                {
                    // Ship out what we have already.
                    this.sendReport(pReport);

                    // Create a new report and mark it as a second report.
                    pReport = null;//new MultiTouchReport((byte)lContacts.Count, false);
                }
             
            }
        }

        /// <summary>
        /// This is called internally to ship out a report.
        /// </summary>
        /// <param name="report"></param>
        private void sendReport(MultiTouchReport pReport)
        {
            // Prime the data for sending.
            pReport.prepareData();

            // Ship it out!
            this.pDevice.WriteReport(pReport,null);
        }




        public void processEventFrame()
        {
            throw new NotImplementedException();
        }

        public void queueContact(WiiContact contact)
        {
            throw new NotImplementedException();
        }
    }
    
    /// <summary>
    /// A helper class which extends the HIDReport type by adding some extra features.
    /// </summary>
    internal class MultiTouchReport : HidReport
    {
        /// <summary>
        /// The number of contacts which can be send per-report.
        /// </summary>
        public const int MaxContactsPerReport = 2;

        /// <summary>
        /// The ID of this type of report.
        /// </summary>
        public const byte ReportIdMultiTouch = 1;

        /// <summary>
        /// Is this the first report in a sequence?
        /// </summary>
        private readonly bool bFirstReport;

        /// <summary>
        /// The total number of contacts the report sequence is delivering.
        /// </summary>
        private readonly byte iTrueContactCount;

        /// <summary>
        /// The size of one report in bytes.
        /// </summary>
        private const int ReportLength = MaxContactsPerReport * (HidContactInfo.HidContactInfoSize) + 2;

        /// <summary>
        /// The list of HID
        /// </summary>
        private List<HidContactInfo> lContacts = null;

        /// <summary>
        /// Call this to construct a new multi-touch report.
        /// </summary>
        /// <param name="iTrueContactCount"></param>
        /// <param name="bFirstReport"></param>
        public MultiTouchReport(byte iTrueContactCount, bool bFirstReport)
            : base(ReportLength)
        {
            // Save the properties and setup the lists etc.
            this.iTrueContactCount = iTrueContactCount;
            this.bFirstReport = bFirstReport;
            this.ReportId  = ReportIdMultiTouch;
            this.lContacts = new List<HidContactInfo>(MaxContactsPerReport);
        }

        /// <summary>
        /// Append a contact to the list of contacts in this report.  Note you cannot call this more than 'MaxContactsPerReport' per report.
        /// </summary>
        /// <param name="pContact">The contact to add.</param>
        public void addContact(HidContactInfo pContact)
        {
            // If we have reached the maximum number of contacts - throw an error.
            if (lContacts.Count == MaxContactsPerReport)
                throw new InvalidOperationException("Cannot add more than " + MaxContactsPerReport + " to a MultiTouchReport.");

            // All is well so add it.
            lContacts.Add(pContact);
        }

        /// <summary>
        /// Prepare the data in this report before it is shipped off to the HID driver.
        /// </summary>
        public void prepareData()
        {
            using (BinaryWriter pWriter = new BinaryWriter(new MemoryStream(Data)))
            {
                // For each contact - get its data represented in the correct format.
                for (int i = 0; i<MaxContactsPerReport && i<this.lContacts.Count;i++)
                {
                    HidContactInfo pContact = this.lContacts[i];
                    byte[] tBuffer = pContact.ToBytes();
                    pWriter.Write(tBuffer);

                }
                
                // Fill any remaining space with 0's.
                int iSpace = MaxContactsPerReport - this.lContacts.Count;
                if (iSpace > 0)
                {
                    byte[] buffer = new byte[(HidContactInfo.HidContactInfoSize) * iSpace];
                    pWriter.Write(buffer);
                }
                // If it is our first report then write the byte which contains the number in the report sequence.
                if (bFirstReport)
                    pWriter.Write((byte)iTrueContactCount);
                else
                    pWriter.Write((byte)0);
            }
        }

        /// <summary>
        /// Return a string representation of this report.
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            // Build a little report thing which describes whats in our list.
            StringBuilder pStringBuilder = new StringBuilder();
            foreach (HidContactInfo contactInfo in this.lContacts)
                pStringBuilder.AppendLine(contactInfo.ToString());
            pStringBuilder.AppendLine();
            return pStringBuilder.ToString();
        }
    }
  
}