import React from 'react';

interface PageStateProps {
  message: string;
  minHeightClassName?: string;
}

export const PageState: React.FC<PageStateProps> = ({
  message,
  minHeightClassName = 'min-h-56',
}) => {
  return <div className={`page-empty ${minHeightClassName}`}>{message}</div>;
};
