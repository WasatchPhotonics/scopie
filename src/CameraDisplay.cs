﻿using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Scopie
{
    class CameraDisplay
    {
        class DoubleBufferedForm : Form
        {
            public DoubleBufferedForm()
            {
                DoubleBuffered = true;
            }
        }

        private readonly QhyCcd _camera;
        private readonly Form _form;
        private readonly Bitmap _bitmap;
        private string _status;

        public CameraDisplay(QhyCcd camera)
        {
            _camera = camera;
            _form = new DoubleBufferedForm();
            _status = "starting...";
            Console.WriteLine(camera.Width);
            Console.WriteLine(camera.Height);
            _bitmap = new Bitmap(camera.Width, camera.Height, PixelFormat.Format32bppArgb);
            _form.Paint += OnPaint;
        }

        public void Start()
        {
            var formThread = new Thread(() => Application.Run(_form));
            formThread.SetApartmentState(ApartmentState.STA);
            formThread.IsBackground = true;
            formThread.Start();

            var imageThread = new Thread(() => DoImage());
            imageThread.IsBackground = true;
            imageThread.Start();
        }

        private void OnPaint(object sender, PaintEventArgs e)
        {
            var calcWidth = _form.Height * _bitmap.Width / _bitmap.Height;
            var calcHeight = _form.Width * _bitmap.Height / _bitmap.Width;
            var realWidth = Math.Min(_form.Width, calcWidth);
            var realHeight = Math.Min(_form.Height, calcHeight);
            e.Graphics.DrawImage(_bitmap, new Rectangle(0, 0, realWidth, realHeight));
            e.Graphics.DrawString(_status, SystemFonts.DefaultFont, Brushes.Red, 1, 1);
        }

        private double Mean(ushort[] data)
        {
            var sum = 0.0;
            foreach (var datum in data)
            {
                sum += datum;
            }
            return sum / data.Length;
        }

        private double Stdev(ushort[] data, double mean)
        {
            var sum = 0.0;
            foreach (var datum in data)
            {
                var diff = (datum - mean);
                sum += diff * diff;
            }
            return Math.Sqrt(sum / data.Length);
        }

        private int[] ProcessImage(ushort[] data)
        {
            var start = Stopwatch.StartNew();
            var mean = Mean(data);
            var stdev = Stdev(data, mean);
            var pixels = new int[data.Length];

            // ((x - mean) / (stdev * size) + 0.5) * 255
            // ((x / (stdev * size) - mean / (stdev * size)) + 0.5) * 255
            // (x / (stdev * size) - mean / (stdev * size) + 0.5) * 255
            // x / (stdev * size) * 255 - mean / (stdev * size) * 255 + 0.5 * 255
            // x * (255 / (stdev * size)) + (-mean / (stdev * size) + 0.5) * 255
            // x * a + b
            const double size = 3.0;
            var a = 255 / (stdev * size);
            var b = 255 * (-mean / (stdev * size) + 0.5);
            for (var i = 0; i < pixels.Length; i++)
            {
                var value = (byte)Math.Max(0.0, Math.Min(255.0, data[i] * a + b));
                pixels[i] = (value << 16) | (value << 8) | value;
            }

            var procMs = start.ElapsedMilliseconds;
            _status = $"Time to proc: {procMs}\nmean: {mean:f3}\nstdev: {stdev:f3}";
            return pixels;
        }

        public void DoImage()
        {
            _camera.StartLive();
            byte[] rawBytePixels = null;
            while (true)
            {
                while (!_camera.GetLive(ref rawBytePixels)) { }
                var rawShortPixels = new ushort[rawBytePixels.Length / 2];
                Buffer.BlockCopy(rawBytePixels, 0, rawShortPixels, 0, rawBytePixels.Length);
                var pixels = ProcessImage(rawShortPixels);
                try
                {
                    _form.BeginInvoke((Action)(() =>
                    {
                        var locked = _bitmap.LockBits(new Rectangle(0, 0, _bitmap.Width, _bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                        Marshal.Copy(pixels, 0, locked.Scan0, locked.Width * locked.Height);
                        _bitmap.UnlockBits(locked);
                        _form.Invalidate();
                    }));
                }
                catch
                {
                    // form is closed
                    break;
                }
            }
            _camera.StopLive();
        }
    }
}