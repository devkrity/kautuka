import { useEffect, useState } from 'react';

export function useProgressiveLimit<T>(items: T[], batchSize = 9) {
  const [limit, setLimit] = useState(batchSize);

  useEffect(() => {
    setLimit(batchSize);
  }, [items.length, batchSize]);

  return {
    visibleItems: items.slice(0, limit),
    hiddenCount: Math.max(0, items.length - limit),
    showMore: () => setLimit((current) => Math.min(items.length, current + batchSize)),
  };
}
