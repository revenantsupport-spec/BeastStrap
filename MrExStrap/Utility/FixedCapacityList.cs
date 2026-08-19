using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeastStrap.Utility
{
    internal class FixedSizeList<T> : List<T>
    {
        public int MaxSize { get; }

        public FixedSizeList(int size)
        {
            MaxSize = size;
        }

        public new void Add(T item)
        {
            // Evict the oldest (index 0), not the newest (Count - 1) — otherwise a full
            // list churns only its last slot and the cap thrashes instead of behaving FIFO.
            if (Count >= MaxSize)
                RemoveAt(0);
            base.Add(item);
        }
    }
}
