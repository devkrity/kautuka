import { cn } from '@/lib/utils';

export function BrandMark({
  word = true,
  className,
  markClassName,
}: {
  word?: boolean;
  className?: string;
  markClassName?: string;
}) {
  return (
    <span className={cn('inline-flex items-center gap-2.5 text-foreground', className)}>
      <img
        src="/assets/kautuka-mark.svg"
        alt=""
        aria-hidden="true"
        width={30}
        height={30}
        className={cn(
          'brand-mark block h-[30px] w-[30px] shrink-0 object-contain',
          markClassName
        )}
      />
      {word ? <span className="brand-wordmark">kautuka</span> : null}
    </span>
  );
}
