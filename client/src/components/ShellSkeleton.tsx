import { Skeleton } from "./ui";
import styles from "./components.module.css";

export function ShellSkeleton() {
  return (
    <div className={styles.skeletonShell} aria-busy="true" aria-label="Loading Mode">
      <div className={styles.skeletonRail}>
        {[0, 1, 2, 3].map((i) => <Skeleton key={i} width={32} height={32} radius={8} />)}
      </div>
      <div className={styles.skeletonList}>
        <Skeleton width="60%" height={16} />
        {[0, 1, 2, 3, 4, 5].map((i) => <Skeleton key={i} width={`${90 - i * 7}%`} height={14} />)}
      </div>
      <div className={styles.skeletonMain}>
        {[0, 1, 2].map((i) => <MessageSkeleton key={i} />)}
      </div>
    </div>
  );
}

export function MessageSkeleton() {
  return (
    <div className={styles.skeletonRow} aria-hidden>
      <Skeleton width={32} height={32} radius={999} />
      <div className={styles.skeletonLines}>
        <Skeleton width={140} height={12} />
        <Skeleton width="80%" height={12} />
        <Skeleton width="55%" height={12} />
      </div>
    </div>
  );
}
